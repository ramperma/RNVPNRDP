using System.Runtime.InteropServices;
using WgRdp.Logging;
using WgRdp.Paths;
using WgRdp.Util;

namespace WgRdp.Linux;

/// <summary>
/// Ejecuta un script bash como root. Pide autorización con pkexec (o sudo si no hay pkexec).
/// 126 = el usuario cerró el diálogo; 127 = autorización denegada.
/// </summary>
public static class RootRunner
{
    public const int Dismissed = 126;
    public const int Denied = 127;

    public static async Task<ProcessResult> RunBashAsync(
        string scriptBody, TimeSpan timeout, CancellationToken ct = default)
    {
        AppPaths.Ensure();
        var scriptPath = Path.Combine(AppPaths.RuntimeDir, "job-" + Guid.NewGuid().ToString("N") + ".sh");
        await File.WriteAllTextAsync(scriptPath, "#!/bin/bash\nset -euo pipefail\n" + scriptBody + "\n", ct)
            .ConfigureAwait(false);
        AppPaths.TryPrivate(scriptPath);
        TryExecutable(scriptPath);

        try
        {
            if (GetEuid() == 0)
                return await ProcessRunner.RunAsync("/bin/bash", [scriptPath], timeout, ct).ConfigureAwait(false);

            var pkexec = LinuxTools.Which("pkexec");
            if (pkexec is not null)
            {
                Log.Info("Se pedirá autorización de administrador (pkexec).");
                var result = await ProcessRunner.RunAsync(pkexec, ["/bin/bash", scriptPath], timeout, ct)
                    .ConfigureAwait(false);
                if (result.ExitCode == Dismissed)
                    Log.Warn("El usuario cerró el diálogo de autorización.");
                else if (result.ExitCode == Denied)
                    Log.Warn("Autorización de administrador denegada.");
                return result;
            }

            var sudo = LinuxTools.Which("sudo");
            if (sudo is not null)
            {
                Log.Info("pkexec no está; se pedirá autorización con sudo.");
                return await ProcessRunner.RunAsync(sudo, ["--", "/bin/bash", scriptPath], timeout, ct)
                    .ConfigureAwait(false);
            }

            return new ProcessResult(-1, "", "No hay pkexec ni sudo para las tareas de administrador.");
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { /* temporal */ }
        }
    }

    public static string BashQuote(string value)
    {
        if (value.Contains('\0') || value.Contains('\n') || value.Contains('\r'))
            throw new InvalidOperationException("El valor contiene un salto de línea y no se puede pasar al script.");
        return "'" + value.Replace("'", "'\\''") + "'";
    }

    private static void TryExecutable(string path)
    {
        try
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch
        {
            // /bin/bash recibe la ruta como argumento; el bit de ejecución no es imprescindible.
        }
    }

    [DllImport("libc")]
    private static extern uint geteuid();

    private static uint GetEuid()
    {
        try { return geteuid(); }
        catch { return 1; }
    }
}
