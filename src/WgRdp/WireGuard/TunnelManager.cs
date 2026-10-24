using WgRdp.Logging;
using WgRdp.Util;

namespace WgRdp.WireGuard;

/// <summary>
/// Gestiona el túnel vía el servicio del cliente WireGuard:
///   wireguard.exe /installtunnelservice &lt;conf&gt;  → crea WireGuardTunnel$&lt;nombre&gt;
///   net start/stop WireGuardTunnel$&lt;nombre&gt;     → conecta/desconecta
///   wg.exe show &lt;nombre&gt;                        → estado del enlace (requiere admin)
/// </summary>
public sealed class TunnelManager
{
    public const int ServiceStopped = 1060; // ERROR_SERVICE_DOES_NOT_EXIST

    public static string TunnelDirectory => Path.Combine(
        WireGuardInstaller.InstalledDirectory ?? @"C:\Program Files\WireGuard", "tunnel");

    public static string TunnelConfPath(string tunnelName) =>
        Path.Combine(TunnelDirectory, tunnelName + ".conf");

    public static string ServiceName(string tunnelName) => "WireGuardTunnel$" + tunnelName;

    private static string WgExe => Path.Combine(WireGuardInstaller.InstalledDirectory!, "wg.exe");
    private static string WireGuardExe => Path.Combine(WireGuardInstaller.InstalledDirectory!, "wireguard.exe");

    public static async Task<bool> TunnelServiceExistsAsync(
        string tunnelName, CancellationToken ct = default) =>
        await QueryServiceStateAsync(tunnelName, ct).ConfigureAwait(false) is not null;

    /// <summary>Devuelve el estado del servicio ("RUNNING", "STOPPED"...) o null si no existe.</summary>
    public static async Task<string?> QueryServiceStateAsync(
        string tunnelName, CancellationToken ct = default)
    {
        // Nunca usar GetResult()/Wait aquí: si se llama desde el hilo de UI de WPF,
        // ProcessRunner captura el SynchronizationContext y se produce un deadlock.
        var r = await ProcessRunner.RunAsync(
            "sc.exe", $"query \"{ServiceName(tunnelName)}\"",
            TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);

        if (r.ExitCode == ServiceStopped)
            return null;

        // sc.exe localiza las etiquetas (STATE vs ESTADO) pero el código numérico es estable:
        // 1 STOPPED, 2 START_PENDING, 3 STOP_PENDING, 4 RUNNING.
        var text = r.StdOut + Environment.NewLine + r.StdErr;
        if (text.Length == 0)
            return null;

        var stateLine = text.Split('\n')
            .FirstOrDefault(l =>
                l.Contains("STATE", StringComparison.OrdinalIgnoreCase)
                || l.Contains("ESTADO", StringComparison.OrdinalIgnoreCase));

        var probe = stateLine ?? text;
        if (ContainsAny(probe, "START_PENDING", "INICIO_PENDIENTE") || HasStateCode(probe, 2))
            return "START_PENDING";
        if (ContainsAny(probe, "STOP_PENDING", "DETENIENDO") || HasStateCode(probe, 3))
            return "STOP_PENDING";
        if (ContainsAny(probe, "RUNNING", "EN_EJECUCION", "EN_EJECUCIÓN") || HasStateCode(probe, 4))
            return "RUNNING";
        if (ContainsAny(probe, "STOPPED", "DETENIDO") || HasStateCode(probe, 1))
            return "STOPPED";
        return "UNKNOWN";
    }

    /// <summary>
    /// Copia la configuración al directorio de túneles e instala el servicio.
    /// Debe ejecutarse elevado. Idempotente: si la config ya coincide (hash), no repite la instalación.
    /// Nota: /installtunnelservice suele dejar el servicio en RUNNING (o START_PENDING).
    /// </summary>
    public static async Task<ProcessResult> ImportTunnelAsync(
        string tunnelName, string sourceConfPath, CancellationToken ct = default)
    {
        if (!File.Exists(sourceConfPath))
            return new ProcessResult(-1, "", $"No existe el archivo de configuración: {sourceConfPath}");

        var target = TunnelConfPath(tunnelName);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        var sourceHash = Sha256(File.ReadAllBytes(sourceConfPath));

        var exists = await TunnelServiceExistsAsync(tunnelName, ct).ConfigureAwait(false);
        if (exists && File.Exists(target) &&
            Sha256(File.ReadAllBytes(target)) == sourceHash)
        {
            Log.Info($"Túnel '{tunnelName}' ya importado y sin cambios. Se omite.");
            return new ProcessResult(0, "", "");
        }

        File.Copy(sourceConfPath, target, overwrite: true);
        Log.Info($"Configuración copiada a {target}");

        if (exists)
        {
            // Reimportar: primero desinstalar el servicio viejo.
            await ProcessRunner.RunAsync(WireGuardExe, $"/uninstalltunnelservice {tunnelName}",
                TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
            // Esperar a que SCM suelte el servicio.
            await WaitForServiceGoneAsync(tunnelName, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        }

        var result = await ProcessRunner.RunAsync(WireGuardExe,
            $"/installtunnelservice \"{target}\"", TimeSpan.FromMinutes(2), ct)
            .ConfigureAwait(false);

        if (result.Success)
            Log.Info($"Servicio de túnel '{tunnelName}' instalado.");
        else
            Log.Error($"Fallo al instalar el servicio de túnel (código {result.ExitCode}): {result.Output}");

        return result;
    }

    /// <summary>
    /// Asegura que el túnel está RUNNING. /installtunnelservice ya lo arranca a menudo:
    /// no hay que llamar a net start a ciegas (falla con "se está iniciando o deteniendo").
    /// </summary>
    public static async Task EnsureTunnelRunningAsync(
        string tunnelName, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        var startAttempted = false;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var state = await QueryServiceStateAsync(tunnelName, ct).ConfigureAwait(false);

            if (state == "RUNNING")
            {
                Log.Info($"Túnel '{tunnelName}' en RUNNING.");
                return;
            }

            if (state is "START_PENDING" or "STOP_PENDING")
            {
                Log.Info($"Túnel '{tunnelName}' en {state}; esperando…");
                await Task.Delay(750, ct).ConfigureAwait(false);
                continue;
            }

            if (state is null)
                throw new InvalidOperationException(
                    $"No existe el servicio {ServiceName(tunnelName)}. Importe el túnel primero.");

            // STOPPED u UNKNOWN → intentar arrancar una vez, luego sondear.
            if (!startAttempted)
            {
                startAttempted = true;
                Log.Info($"Conectando túnel '{tunnelName}'…");
                var start = await ProcessRunner.RunAsync(
                    "net.exe", $"start \"{ServiceName(tunnelName)}\"",
                    TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);

                if (start.Success
                    || start.Output.Contains("already been started", StringComparison.OrdinalIgnoreCase)
                    || start.Output.Contains("ya se ha iniciado", StringComparison.OrdinalIgnoreCase)
                    || start.Output.Contains("ya está iniciado", StringComparison.OrdinalIgnoreCase)
                    || start.Output.Contains("starting or stopping", StringComparison.OrdinalIgnoreCase)
                    || start.Output.Contains("iniciando o deteniendo", StringComparison.OrdinalIgnoreCase))
                {
                    await Task.Delay(750, ct).ConfigureAwait(false);
                    continue;
                }

                throw new InvalidOperationException(
                    $"No se pudo arrancar el túnel: {start.Output}");
            }

            await Task.Delay(750, ct).ConfigureAwait(false);
        }

        var final = await QueryServiceStateAsync(tunnelName, ct).ConfigureAwait(false);
        throw new InvalidOperationException(
            $"Timeout esperando RUNNING del túnel '{tunnelName}' (estado={final ?? "null"}).");
    }

    public static async Task<ProcessResult> StopTunnelAsync(string tunnelName, CancellationToken ct = default)
    {
        Log.Info($"Desconectando túnel '{tunnelName}'...");
        return await ProcessRunner.RunAsync("net.exe", $"stop \"{ServiceName(tunnelName)}\"",
            TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
    }

    public static async Task<ProcessResult> RemoveTunnelAsync(string tunnelName, CancellationToken ct = default)
    {
        Log.Info($"Eliminando el túnel '{tunnelName}'...");
        return await ProcessRunner.RunAsync(WireGuardExe,
            $"/uninstalltunnelservice {tunnelName}", TimeSpan.FromMinutes(2), ct)
            .ConfigureAwait(false);
    }

    /// <summary>Salida de `wg.exe show &lt;tunel&gt;`. Vacío si no hay acceso o el túnel no está arriba.</summary>
    public static async Task<string> ShowAsync(string tunnelName, CancellationToken ct = default)
    {
        var r = await ProcessRunner.RunAsync(WgExe, $"show {tunnelName}", TimeSpan.FromSeconds(15), ct)
            .ConfigureAwait(false);
        return r.Success ? r.StdOut.Trim() : "";
    }

    private static async Task WaitForServiceGoneAsync(
        string tunnelName, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!await TunnelServiceExistsAsync(tunnelName, ct).ConfigureAwait(false))
                return;
            await Task.Delay(400, ct).ConfigureAwait(false);
        }
    }

    private static string Sha256(byte[] data)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(data));
    }

    private static bool ContainsAny(string text, params string[] tokens) =>
        tokens.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));

    private static bool HasStateCode(string line, int code) =>
        System.Text.RegularExpressions.Regex.IsMatch(line, $@":\s*{code}\b");
}
