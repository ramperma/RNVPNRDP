using System.Globalization;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using WgRdp.Logging;
using WgRdp.Util;

namespace WgRdp.Linux;

/// <summary>Comprueba handshake, ping o el puerto RDP antes de abrir FreeRDP.</summary>
public static partial class TunnelHealthCheck
{
    public static async Task<bool> WaitForLinkAsync(
        string tunnelName,
        string? serverVpnIp,
        string? rdpHost,
        int rdpPort,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        var wg = LinuxTools.Which("wg");
        var port = rdpPort is < 1 or > 65535 ? 3389 : rdpPort;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (wg is not null)
            {
                var show = await ProcessRunner.RunAsync(wg, ["show", tunnelName], TimeSpan.FromSeconds(8), ct)
                    .ConfigureAwait(false);
                if (show.Success && HandshakeIsFresh(show.StdOut))
                {
                    Log.Info("Handshake WireGuard confirmado.");
                    return true;
                }
            }

            if (!string.IsNullOrWhiteSpace(serverVpnIp) && await PingOnceAsync(serverVpnIp, ct).ConfigureAwait(false))
            {
                Log.Info($"Ping OK a {serverVpnIp} por el túnel.");
                return true;
            }

            if (!string.IsNullOrWhiteSpace(rdpHost) && await TcpProbeAsync(rdpHost, port, ct).ConfigureAwait(false))
            {
                Log.Info($"Host RDP {rdpHost}:{port} alcanzable.");
                return true;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }

        Log.Warn($"Timeout esperando enlace del túnel '{tunnelName}'.");
        return false;
    }

    private static bool HandshakeIsFresh(string show)
    {
        // Salida típica de `wg show` (en inglés): «latest handshake: 45 seconds ago»,
        // «2 minutes ago» o «1 minute, 23 seconds ago». El código antiguo buscaba «second»
        // en singular y no coincidía con «seconds».
        var idx = show.IndexOf("latest handshake:", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return false;

        var line = show[idx..];
        var nl = line.IndexOf('\n');
        if (nl > 0)
            line = line[..nl];

        if (line.Contains("(never)", StringComparison.OrdinalIgnoreCase))
            return false;

        var minM = Regex.Match(line, @"(\d+)\s+minutes?", RegexOptions.IgnoreCase);
        var secM = Regex.Match(line, @"(\d+)\s+seconds?", RegexOptions.IgnoreCase);
        if (!minM.Success && !secM.Success)
            return false;

        var total = 0;
        if (minM.Success)
            total += int.Parse(minM.Groups[1].Value, CultureInfo.InvariantCulture) * 60;
        if (secM.Success)
            total += int.Parse(secM.Groups[1].Value, CultureInfo.InvariantCulture);

        return total is > 0 and < 180;
    }

    private static async Task<bool> PingOnceAsync(string host, CancellationToken ct)
    {
        if (!IsSafeHost(host))
            return false;
        var ping = LinuxTools.Which("ping");
        if (ping is null)
            return false;
        var result = await ProcessRunner.RunAsync(ping, ["-c", "1", "-W", "2", host], TimeSpan.FromSeconds(8), ct)
            .ConfigureAwait(false);
        return result.Success;
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

    private static bool IsSafeHost(string host) =>
        host.Length is > 0 and <= 253
        && host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or ':' or '-' or '%');
}
