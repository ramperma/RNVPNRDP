using System.Net.Sockets;
using System.Text.RegularExpressions;
using WgRdp.Logging;
using WgRdp.Util;

namespace WgRdp.WireGuard;

/// <summary>Comprueba que el túnel está realmente operativo antes de lanzar el RDP.</summary>
public static partial class TunnelHealthCheck
{
    [GeneratedRegex(@"latest handshake:\s*(\d+)\s*second")]
    private static partial Regex HandshakeRegex();

    /// <summary>
    /// Espera a que el túnel esté usable. En Windows, `wg show` requiere admin;
    /// si no hay permiso, se usa ping / sonda TCP al host RDP.
    /// </summary>
    public static async Task<bool> WaitForHandshakeAsync(
        string tunnelName,
        string? serverVpnIp,
        string? rdpHost,
        int rdpPort,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var show = await TunnelManager.ShowAsync(tunnelName, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(show))
            {
                var m = HandshakeRegex().Match(show);
                // Handshake de menos de 3 minutos = enlace vivo (el keepalive típico es 25 s).
                if (m.Success && int.TryParse(m.Groups[1].Value, out var secs) && secs < 180)
                {
                    Log.Info($"Handshake WireGuard confirmado ({secs} s).");
                    return true;
                }

                // Interfaz visible pero aún sin handshake: seguir esperando.
                if (show.Contains("interface:", StringComparison.OrdinalIgnoreCase)
                    || show.Contains("listening port", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Info("Interfaz WireGuard visible; esperando handshake…");
                }
            }

            if (!string.IsNullOrWhiteSpace(serverVpnIp)
                && await PingOnceAsync(serverVpnIp, ct).ConfigureAwait(false))
            {
                Log.Info($"Ping OK a {serverVpnIp} por el túnel (salud alternativa a wg show).");
                return true;
            }

            if (!string.IsNullOrWhiteSpace(rdpHost)
                && await TcpProbeAsync(rdpHost, rdpPort <= 0 ? 3389 : rdpPort, ct).ConfigureAwait(false))
            {
                Log.Info($"Host RDP {rdpHost}:{rdpPort} alcanzable por el túnel.");
                return true;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }

        Log.Warn($"Timeout esperando enlace del túnel '{tunnelName}'.");
        return false;
    }

    /// <summary>Ping a la IP del servidor por el lado del túnel (si el perfil la define).</summary>
    public static async Task<bool> PingServerVpnIpAsync(
        string? serverVpnIp, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(serverVpnIp))
            return true;

        if (await PingOnceAsync(serverVpnIp, ct).ConfigureAwait(false))
        {
            Log.Info($"Ping OK a {serverVpnIp} por el túnel.");
            return true;
        }

        Log.Warn($"Ping a {serverVpnIp} falló (puede estar filtrado por firewall; se continúa).");
        return true; // Informativo: no bloqueamos si el firewall lo filtra.
    }

    private static async Task<bool> PingOnceAsync(string host, CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync(
            "ping.exe", $"-n 1 -w 1500 {host}", TimeSpan.FromSeconds(10), ct)
            .ConfigureAwait(false);
        return r.Success;
    }

    private static async Task<bool> TcpProbeAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromSeconds(2));
            await client.ConnectAsync(host, port, linked.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
