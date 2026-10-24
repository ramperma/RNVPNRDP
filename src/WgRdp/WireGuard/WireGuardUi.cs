using System.Diagnostics;
using Microsoft.Win32;
using WgRdp.Logging;
using WgRdp.Util;

namespace WgRdp.WireGuard;

/// <summary>
/// El túnel lo arranca WG-RDP como servicio (<c>WireGuardTunnel$</c>). La ventana/bandeja
/// de WireGuard no hace falta y suele mostrar "desactivado" aunque el túnel esté arriba.
/// </summary>
public static class WireGuardUi
{
    /// <summary>
    /// Quita el arranque automático de la UI y cierra el proceso de usuario.
    /// No toca los servicios de túnel (corren en la sesión 0).
    /// </summary>
    public static async Task StopUserInterfaceAsync(CancellationToken ct = default)
    {
        RemoveLogonHooks();
        for (var i = 0; i < 8; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (!KillUserSessionGui())
                break;
            await Task.Delay(400, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Mientras WG-RDP está abierto, impide que reaparezca la bandeja de WireGuard.
    /// </summary>
    public static async Task WatchAndStopUserInterfaceAsync(CancellationToken ct)
    {
        RemoveLogonHooks();
        while (!ct.IsCancellationRequested)
        {
            KillUserSessionGui();
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Desinstala el servicio del administrador de WireGuard. Requiere elevación.
    /// </summary>
    public static async Task DisableManagerElevatedAsync(CancellationToken ct = default)
    {
        var exe = Path.Combine(WireGuardInstaller.InstalledDirectory ?? "", "wireguard.exe");
        if (!File.Exists(exe))
            return;

        Log.Info("Desinstalando el administrador gráfico de WireGuard (no hace falta para el túnel).");
        await ProcessRunner.RunAsync(exe, "/uninstallmanagerservice", TimeSpan.FromMinutes(1), ct)
            .ConfigureAwait(false);
        await ProcessRunner.RunAsync("sc.exe", "stop WireGuardManager", TimeSpan.FromSeconds(20), ct)
            .ConfigureAwait(false);

        RemoveLogonHooks();
        KillUserSessionGui();
    }

    private static void RemoveLogonHooks()
    {
        RemoveRunValues(Registry.CurrentUser);
        RemoveRunValues(Registry.LocalMachine);
        RemoveStartupShortcuts(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup));
        RemoveStartupShortcuts(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup));
    }

    private static void RemoveRunValues(RegistryKey root)
    {
        try
        {
            using var key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key is null)
                return;

            foreach (var name in key.GetValueNames())
            {
                var value = key.GetValue(name)?.ToString() ?? "";
                if (!value.Contains("wireguard.exe", StringComparison.OrdinalIgnoreCase))
                    continue;

                key.DeleteValue(name, throwOnMissingValue: false);
                Log.Info($"Arranque automático de WireGuard eliminado ({root.Name}\\…\\Run\\{name}).");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo limpiar el arranque de WireGuard en {root.Name}: {ex.Message}");
        }
    }

    private static void RemoveStartupShortcuts(string folder)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            return;

        foreach (var file in Directory.GetFiles(folder, "*WireGuard*"))
        {
            try
            {
                File.Delete(file);
                Log.Info($"Acceso de inicio de WireGuard eliminado: {file}");
            }
            catch (Exception ex)
            {
                Log.Warn($"No se pudo borrar {file}: {ex.Message}");
            }
        }
    }

    /// <returns>True si había algún proceso de UI que matar.</returns>
    private static bool KillUserSessionGui()
    {
        var session = Process.GetCurrentProcess().SessionId;
        var killed = false;

        foreach (var process in Process.GetProcessesByName("wireguard"))
        {
            try
            {
                // Los servicios de túnel viven en la sesión 0; no tocarlos.
                if (process.SessionId != session)
                    continue;

                Log.Info($"Cerrando la interfaz de WireGuard (pid {process.Id}); el túnel lo gestiona WG-RDP.");
                process.Kill(entireProcessTree: false);
                killed = true;
            }
            catch (Exception ex)
            {
                Log.Warn($"No se pudo cerrar wireguard.exe pid {process.Id}: {ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }

        return killed;
    }
}
