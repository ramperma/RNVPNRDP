using System.Diagnostics;
using System.Text;
using WgRdp.Logging;
using WgRdp.Profiles;
using WgRdp.Util;

namespace WgRdp.Rdp;

/// <summary>
/// Sesión RDP con mstsc.exe (ventana propia). Se usa si el control ActiveX no está
/// registrado. Las credenciales van a cmdkey, no a un .rdp en claro.
/// </summary>
public sealed class MstscSession : IDisposable
{
    private Process? _process;
    private string? _cmdKeyTarget;
    private string? _rdpFile;
    private bool _ended;

    public event Action<bool>? Ended;

    public async Task StartAsync(Profile profile, string password)
    {
        var host = profile.RdpHost.Trim();
        var port = profile.RdpPort is < 1 or > 65535 ? 3389 : profile.RdpPort;
        _cmdKeyTarget = "TERMSRV/" + host;

        var user = string.IsNullOrWhiteSpace(profile.Domain)
            ? profile.Username
            : profile.Domain.Trim() + "\\" + profile.Username.Trim();

        var cmd = await ProcessRunner.RunAsync(
            "cmdkey.exe",
            $"/generic:{Quote(_cmdKeyTarget)} /user:{Quote(user)} /pass:{Quote(password)}",
            TimeSpan.FromSeconds(15)).ConfigureAwait(true);

        if (!cmd.Success)
            Log.Warn($"cmdkey no pudo guardar la credencial: {cmd.Output}");

        _rdpFile = WriteRdpFile(profile, host, port);

        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "mstsc.exe"),
            Arguments = Quote(_rdpFile),
            UseShellExecute = false,
        };

        _process = Process.Start(psi)
            ?? throw new InvalidOperationException("No se pudo iniciar mstsc.exe.");

        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => Finish(failed: false);
        Log.Info(
            $"mstsc iniciado (pid {_process.Id}) hacia {host}:{port}. " +
            "Recursos locales: impresoras=" + (profile.RedirectPrinters ? "sí" : "no") +
            ", discos=" + (profile.RedirectDrives ? "sí" : "no") +
            ", portapapeles=" + (profile.RedirectClipboard ? "sí" : "no") + ".");
    }

    /// <summary>
    /// Archivo .rdp temporal sin contraseña (va en cmdkey). Activa impresoras, discos y portapapeles.
    /// </summary>
    private static string WriteRdpFile(Profile profile, string host, int port)
    {
        var path = Path.Combine(Path.GetTempPath(), "wgrdp-" + Guid.NewGuid().ToString("N") + ".rdp");
        var user = string.IsNullOrWhiteSpace(profile.Domain)
            ? profile.Username
            : profile.Domain.Trim() + "\\" + profile.Username.Trim();

        var sb = new StringBuilder();
        sb.AppendLine($"full address:s:{host}:{port}");
        sb.AppendLine($"username:s:{user}");
        sb.AppendLine($"authentication level:i:{profile.AuthenticationLevel}");
        sb.AppendLine("enablecredsspsupport:i:1");
        sb.AppendLine("prompt for credentials:i:0");
        sb.AppendLine($"session bpp:i:{profile.ColorDepth}");
        sb.AppendLine($"redirectprinters:i:{(profile.RedirectPrinters ? 1 : 0)}");
        sb.AppendLine($"redirectclipboard:i:{(profile.RedirectClipboard ? 1 : 0)}");
        sb.AppendLine($"drivestoredirect:s:{(profile.RedirectDrives ? "*" : "")}");
        File.WriteAllText(path, sb.ToString(), Encoding.Unicode);
        return path;
    }

    public void Disconnect()
    {
        try
        {
            if (_process is { HasExited: false })
                _process.CloseMainWindow();
        }
        catch { /* cierre */ }
    }

    private void Finish(bool failed)
    {
        if (_ended) return;
        _ended = true;
        DeleteCmdKey();
        DeleteRdpFile();
        Ended?.Invoke(failed);
    }

    private void DeleteCmdKey()
    {
        if (string.IsNullOrEmpty(_cmdKeyTarget))
            return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmdkey.exe",
                Arguments = $"/delete:{Quote(_cmdKeyTarget)}",
                UseShellExecute = false,
                CreateNoWindow = true,
            })?.Dispose();
        }
        catch { /* ignore */ }
    }

    private void DeleteRdpFile()
    {
        if (string.IsNullOrEmpty(_rdpFile))
            return;
        try { File.Delete(_rdpFile); } catch { /* ignore */ }
        _rdpFile = null;
    }

    public void Dispose()
    {
        Disconnect();
        DeleteCmdKey();
        DeleteRdpFile();
        _process?.Dispose();
        _process = null;
    }

    private static string Quote(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "\"\"";
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}
