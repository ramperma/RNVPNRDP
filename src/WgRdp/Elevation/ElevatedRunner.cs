using System.ComponentModel;
using System.Diagnostics;
using WgRdp.Logging;
using WgRdp.WireGuard;

namespace WgRdp.Elevation;

/// <summary>
/// Pasos privilegiados sin elevar la app entera: relanza el MISMO exe con
/// "--elevated &lt;comando&gt;" usando el verbo runas (prompt UAC) y espera el código de salida.
/// Si el exe está en una unidad de red/mapeada (p. ej. Z:), UAC no la ve: se usa una
/// copia local en %ProgramData%\WgRdp\elevated-bin, o msiexec directo para instalar.
/// </summary>
public static class ElevatedRunner
{
    public const string Marker = "--elevated";

    private static readonly string ElevatedBinDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "WgRdp", "elevated-bin");

    public static bool IsElevatedMode(string[] args) =>
        args.Length >= 2 && args[0] == Marker;

    /// <summary>Ejecuta el comando dentro de la instancia elevada. Devuelve código de salida.</summary>
    public static async Task<int> ExecuteElevatedCommandAsync(string[] args)
    {
        // args = [--elevated, comando, ...datos]
        var command = args[1];
        Log.Info($"Instancia elevada: comando '{command}'.");

        try
        {
            switch (command)
            {
                case "install-wireguard":
                {
                    if (!WireGuardInstaller.IsWgCliInstalled())
                    {
                        var r = await WireGuardInstaller.InstallSilentlyAsync();
                        if (r.ExitCode != 0)
                            return r.ExitCode;
                    }
                    await WireGuardUi.DisableManagerElevatedAsync();
                    return 0;
                }
                case "import-tunnel":
                {
                    if (args.Length < 4)
                        return 64;
                    var r = await TunnelManager.ImportTunnelAsync(args[2], args[3]);
                    await WireGuardUi.StopUserInterfaceAsync();
                    return r.ExitCode;
                }
                case "remove-tunnel":
                {
                    if (args.Length < 3)
                        return 64;
                    var r = await TunnelManager.RemoveTunnelAsync(args[2]);
                    return r.ExitCode;
                }
                default:
                    Log.Error($"Comando elevado desconocido: {command}");
                    return 64;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Fallo en comando elevado", ex);
            return 1;
        }
    }

    /// <summary>Lanza la instancia elevada y espera su finalización.</summary>
    public static async Task<int> RunElevatedAsync(params string[] commandAndArgs)
    {
        if (commandAndArgs.Length >= 1
            && string.Equals(commandAndArgs[0], "install-wireguard", StringComparison.Ordinal))
        {
            // msiexec está en System32 (local); el MSI ya está en ProgramData.
            // Evita relanzar WgRdp.exe desde una unidad mapeada que UAC no ve.
            return await RunElevatedMsiexecAsync();
        }

        var exe = ResolveExeForElevation();
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = string.Join(' ', new[] { Marker }.Concat(commandAndArgs).Select(Quote)),
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };

        Log.Info($"Elevando: {exe} {string.Join(' ', commandAndArgs)}");

        try
        {
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("No se pudo iniciar la instancia elevada.");

            await process.WaitForExitAsync();

            if (process.ExitCode == 1223)
                Log.Warn("El usuario canceló la elevación (UAC).");

            return process.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is 3 or 2)
        {
            throw new InvalidOperationException(
                "No se pudo elevar la aplicación (UAC). Si corre desde una unidad de red " +
                $"(p. ej. Z:\\), copie el proyecto a un disco local o use la carpeta {ElevatedBinDirectory}. " +
                $"Detalle: {ex.Message}", ex);
        }
    }

    /// <summary>Instala el MSI de WireGuard elevando msiexec directamente.</summary>
    private static async Task<int> RunElevatedMsiexecAsync()
    {
        var wgExe = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "WireGuard", "wireguard.exe");

        if (WireGuardInstaller.IsWgCliInstalled())
        {
            Log.Info("WireGuard ya está instalado; se omite msiexec.");
            await WireGuardUi.StopUserInterfaceAsync().ConfigureAwait(false);
            return 0;
        }

        var msi = WireGuardInstaller.FindAnyInstaller()
            ?? throw new InvalidOperationException(
                "No hay instalador MSI local. Descárguelo antes de instalar.");

        msi = EnsureLocalFile(msi, WireGuardInstaller.CacheDirectory);
        Directory.CreateDirectory(WireGuardInstaller.CacheDirectory);
        var script = Path.Combine(WireGuardInstaller.CacheDirectory, "install-wg-silent.cmd");
        File.WriteAllText(script,
            "@echo off" + Environment.NewLine +
            "msiexec.exe /i \"" + msi + "\" /qn /norestart" + Environment.NewLine +
            "set ERR=%ERRORLEVEL%" + Environment.NewLine +
            "if exist \"" + wgExe + "\" \"" + wgExe + "\" /uninstallmanagerservice" + Environment.NewLine +
            "exit /b %ERR%" + Environment.NewLine);

        Log.Info($"Elevando instalación silenciosa de WireGuard: {msi}");

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c \"" + script + "\"",
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = WireGuardInstaller.CacheDirectory,
        };

        try
        {
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("No se pudo iniciar msiexec elevado.");

            await process.WaitForExitAsync();

            if (process.ExitCode == 1223)
                Log.Warn("El usuario canceló la elevación (UAC).");
            else if (process.ExitCode != 0)
                Log.Error($"msiexec terminó con código {process.ExitCode}.");
            else
                Log.Info("msiexec completado correctamente.");

            await WireGuardUi.StopUserInterfaceAsync().ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log.Warn("El usuario canceló la elevación (UAC).");
            return 1223;
        }
    }

    /// <summary>
    /// Si el exe no es usable bajo UAC (unidad de red/mapeada/UNC), copia el directorio
    /// de salida a %ProgramData%\WgRdp\elevated-bin y devuelve esa ruta.
    /// </summary>
    private static string ResolveExeForElevation()
    {
        var exe = Environment.ProcessPath
                  ?? Process.GetCurrentProcess().MainModule?.FileName
                  ?? throw new InvalidOperationException("No se pudo determinar la ruta del propio exe.");

        if (IsUsableUnderElevation(exe))
            return exe;

        var srcDir = Path.GetDirectoryName(exe)
            ?? throw new InvalidOperationException("No se pudo determinar el directorio del exe.");

        Directory.CreateDirectory(ElevatedBinDirectory);
        Log.Info($"Exe en ruta no usable por UAC ({exe}); copiando a {ElevatedBinDirectory}");

        foreach (var src in Directory.EnumerateFiles(srcDir))
        {
            var name = Path.GetFileName(src);
            var dest = Path.Combine(ElevatedBinDirectory, name);
            try
            {
                var srcInfo = new FileInfo(src);
                var destInfo = new FileInfo(dest);
                if (!destInfo.Exists
                    || destInfo.Length != srcInfo.Length
                    || destInfo.LastWriteTimeUtc < srcInfo.LastWriteTimeUtc)
                {
                    File.Copy(src, dest, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"No se pudo copiar {name}: {ex.Message}");
            }
        }

        var staged = Path.Combine(ElevatedBinDirectory, Path.GetFileName(exe));
        if (!File.Exists(staged))
            throw new InvalidOperationException(
                $"No se pudo preparar la copia local para elevación en {ElevatedBinDirectory}.");

        return staged;
    }

    /// <summary>True si la ruta está en un disco fijo local (UAC puede abrirla).</summary>
    private static bool IsUsableUnderElevation(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\", StringComparison.Ordinal))
                return false;

            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root))
                return false;

            var drive = new DriveInfo(root);
            return drive.DriveType == DriveType.Fixed;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Si el archivo no está en disco fijo, lo copia a <paramref name="localDir"/>.</summary>
    private static string EnsureLocalFile(string path, string localDir)
    {
        if (IsUsableUnderElevation(path))
            return path;

        Directory.CreateDirectory(localDir);
        var dest = Path.Combine(localDir, Path.GetFileName(path));
        File.Copy(path, dest, overwrite: true);
        Log.Info($"Archivo copiado a ruta local para elevación: {dest}");
        return dest;
    }

    private static string Quote(string arg) =>
        arg.Contains(' ') || arg.Contains('"')
            ? "\"" + arg.Replace("\"", "\\\"") + "\""
            : arg;
}
