using System.Text.RegularExpressions;
using WgRdp.Logging;
using WgRdp.Paths;
using WgRdp.Util;

namespace WgRdp.Linux;

/// <summary>
/// Túnel con wg-quick: copia el .conf a /etc/wireguard/&lt;nombre&gt;.conf y lo sube o baja.
/// Esas dos operaciones necesitan root.
/// </summary>
public static partial class TunnelManager
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_-]{0,14}$")]
    private static partial Regex NameRegex();

    public static async Task UpAsync(
        string tunnelName,
        string sourceConfPath,
        CancellationToken ct,
        IReadOnlyList<string>? tunnelsToCloseFirst = null)
    {
        ValidateName(tunnelName);
        var staged = StageConf(tunnelName, sourceConfPath);
        var wgQuick = LinuxTools.Which("wg-quick")
            ?? throw new InvalidOperationException("No está wg-quick. Instale wireguard-tools.");
        var ip = LinuxTools.Which("ip") ?? "ip";

        var activeOthers = new List<string>();
        if (tunnelsToCloseFirst is { Count: > 0 })
        {
            foreach (var other in tunnelsToCloseFirst
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.Equals(other, tunnelName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!NameRegex().IsMatch(other))
                    continue;
                if (await IsUpAsync(other, ct).ConfigureAwait(false))
                    activeOthers.Add(other);
            }
        }

        var closeOthersScript = string.Join(
            '\n',
            activeOthers.Select(other =>
                $"if {RootRunner.BashQuote(ip)} link show {RootRunner.BashQuote(other)} >/dev/null 2>&1; then\n" +
                $"  {RootRunner.BashQuote(wgQuick)} down {RootRunner.BashQuote(other)} || true\n" +
                "fi"));

        var script = $$"""
            {{closeOthersScript}}
            name={{RootRunner.BashQuote(tunnelName)}}
            src={{RootRunner.BashQuote(staged)}}
            dest="/etc/wireguard/${name}.conf"
            install -d -m 755 /etc/wireguard
            if [[ -f "$dest" ]] && cmp -s "$src" "$dest"; then
              if {{RootRunner.BashQuote(ip)}} link show "$name" >/dev/null 2>&1; then
                echo "Túnel ya activo y sin cambios."
                exit 0
              fi
            fi
            if {{RootRunner.BashQuote(ip)}} link show "$name" >/dev/null 2>&1; then
              {{RootRunner.BashQuote(wgQuick)}} down "$name" || true
            fi
            install -m 600 "$src" "$dest"
            {{RootRunner.BashQuote(wgQuick)}} up "$name"
            """;

        if (activeOthers.Count > 0)
            Log.Info($"Cerrando túnel(es) previos ({string.Join(", ", activeOthers)}) antes de levantar '{tunnelName}'…");
        Log.Info($"Conectando túnel '{tunnelName}'…");
        var result = await RootRunner.RunBashAsync(script, TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
        LogSafe(result.Output);
        if (result.ExitCode is RootRunner.Dismissed or RootRunner.Denied)
            throw new InvalidOperationException("Se canceló o denegó la autorización para levantar el túnel.");
        if (!result.Success)
            throw new InvalidOperationException("No se pudo levantar el túnel: " + LastLines(result.Output));
        Log.Info($"Túnel '{tunnelName}' activo.");
    }

    public static async Task<bool> IsUpAsync(string tunnelName, CancellationToken ct = default)
    {
        ValidateName(tunnelName);
        var ip = LinuxTools.Which("ip") ?? "ip";
        var result = await ProcessRunner.RunAsync(ip, ["link", "show", tunnelName], TimeSpan.FromSeconds(5), ct)
            .ConfigureAwait(false);
        return result.Success;
    }

    public static async Task DownAsync(string tunnelName, bool removeConfig, CancellationToken ct)
    {
        ValidateName(tunnelName);
        var wgQuick = LinuxTools.Which("wg-quick") ?? "wg-quick";
        var ip = LinuxTools.Which("ip") ?? "ip";
        var remove = removeConfig ? "1" : "0";

        var script = $$"""
            name={{RootRunner.BashQuote(tunnelName)}}
            if {{RootRunner.BashQuote(ip)}} link show "$name" >/dev/null 2>&1; then
              {{RootRunner.BashQuote(wgQuick)}} down "$name"
            fi
            if [[ "{{remove}}" == "1" ]]; then
              rm -f "/etc/wireguard/${name}.conf"
            fi
            """;

        Log.Info(removeConfig
            ? $"Desconectando y eliminando el túnel '{tunnelName}'…"
            : $"Desconectando túnel '{tunnelName}'…");
        var result = await RootRunner.RunBashAsync(script, TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
        if (!result.Success && result.ExitCode is not (RootRunner.Dismissed or RootRunner.Denied))
            Log.Error("No se pudo bajar el túnel: " + LastLines(result.Output));
        else if (result.Success)
            Log.Info($"Túnel '{tunnelName}' detenido.");
    }

    private static string StageConf(string tunnelName, string sourceConfPath)
    {
        if (!File.Exists(sourceConfPath))
            throw new FileNotFoundException("No existe la configuración WireGuard.", sourceConfPath);

        var text = File.ReadAllText(sourceConfPath);
        if (!text.Contains("[Interface]", StringComparison.OrdinalIgnoreCase)
            || !text.Contains("[Peer]", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "La configuración no parece de WireGuard (falta [Interface] o [Peer]).");

        AppPaths.Ensure();
        var staged = Path.Combine(AppPaths.TunnelStageDir, tunnelName + ".conf");
        File.Copy(sourceConfPath, staged, overwrite: true);
        AppPaths.TryPrivate(staged);
        return staged;
    }

    public static void ValidateName(string tunnelName)
    {
        if (!NameRegex().IsMatch(tunnelName))
            throw new InvalidOperationException(
                "El nombre del túnel solo puede tener letras, números, guion y guion bajo (15 caracteres como máximo).");
    }

    private static void LogSafe(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;
            if (trimmed.Contains("PrivateKey", StringComparison.OrdinalIgnoreCase))
                continue;
            Log.Info(trimmed);
        }
    }

    private static string LastLines(string text)
    {
        var lines = text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(Environment.NewLine, lines.TakeLast(12));
    }
}
