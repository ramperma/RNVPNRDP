using System.Net.Sockets;
using System.Text.RegularExpressions;
using WgRdp.Logging;
using WgRdp.Util;

namespace WgRdp.Linux;

/// <summary>Comprueba handshake, ping o el puerto RDP antes de abrir FreeRDP.</summary>
public static partial class TunnelHealthCheck
{
    [GeneratedRegex(@"latest handshake:\s*(\d+)\s+second", RegexOptions.IgnoreCase)]
    private static partial Regex SecondsRegex();

    [GeneratedRegex(@"latest handshake:\s*(\d+)\s+minute", RegexOptions.IgnoreCase)]
    private static partial Regex MinutesRegex();

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
        var seconds = SecondsRegex().Match(show);
        if (seconds.Success && int.TryParse(seconds.Groups[1].Value, out var secs))
            return secs < 180;

        var minutes = MinutesRegex().Match(show);
        return minutes.Success
               && int.TryParse(minutes.Groups[1].Value, out var mins)
               && mins < 3;
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
