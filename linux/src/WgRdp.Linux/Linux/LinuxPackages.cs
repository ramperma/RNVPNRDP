using WgRdp.Logging;
using WgRdp.Util;

namespace WgRdp.Linux;

/// <summary>Instala wireguard-tools, FreeRDP y libsecret-tools con el gestor de paquetes de la distro.</summary>
public static class LinuxPackages
{
    public static async Task<ProcessResult> InstallMissingAsync(
        ToolStatus status, CancellationToken ct = default)
    {
        var needWg = status.WireGuard ? "0" : "1";
        var needRdp = status.FreeRdp ? "0" : "1";
        var needSecret = status.SecretTool ? "0" : "1";

        var script = $$"""
            export DEBIAN_FRONTEND=noninteractive
            need_wg={{needWg}}
            need_rdp={{needRdp}}
            need_secret={{needSecret}}

            if command -v apt-get >/dev/null 2>&1; then
              apt-get update
              pkgs=()
              if [[ "$need_wg" == 1 ]]; then pkgs+=(wireguard wireguard-tools); fi
              if [[ "$need_rdp" == 1 ]]; then pkgs+=(freerdp3-x11); fi
              if [[ ${#pkgs[@]} -gt 0 ]]; then apt-get install -y "${pkgs[@]}"; fi
              if [[ "$need_rdp" == 1 ]] && ! command -v xfreerdp >/dev/null 2>&1 && ! command -v wlfreerdp >/dev/null 2>&1; then
                apt-get install -y freerdp-x11
              fi
              if [[ "$need_secret" == 1 ]]; then apt-get install -y libsecret-tools || true; fi
            elif command -v dnf >/dev/null 2>&1; then
              pkgs=()
              if [[ "$need_wg" == 1 ]]; then pkgs+=(wireguard-tools); fi
              if [[ "$need_rdp" == 1 ]]; then pkgs+=(freerdp); fi
              if [[ "$need_secret" == 1 ]]; then pkgs+=(libsecret); fi
              if [[ ${#pkgs[@]} -gt 0 ]]; then dnf install -y "${pkgs[@]}"; fi
            elif command -v pacman >/dev/null 2>&1; then
              pkgs=()
              if [[ "$need_wg" == 1 ]]; then pkgs+=(wireguard-tools); fi
              if [[ "$need_rdp" == 1 ]]; then pkgs+=(freerdp); fi
              if [[ "$need_secret" == 1 ]]; then pkgs+=(libsecret); fi
              if [[ ${#pkgs[@]} -gt 0 ]]; then pacman -Sy --noconfirm "${pkgs[@]}"; fi
            elif command -v zypper >/dev/null 2>&1; then
              pkgs=()
              if [[ "$need_wg" == 1 ]]; then pkgs+=(wireguard-tools); fi
              if [[ "$need_rdp" == 1 ]]; then pkgs+=(freerdp); fi
              if [[ "$need_secret" == 1 ]]; then pkgs+=(libsecret-tools); fi
              if [[ ${#pkgs[@]} -gt 0 ]]; then zypper --non-interactive install "${pkgs[@]}"; fi
            else
              echo "No se reconoce el gestor de paquetes. Instale wireguard-tools y freerdp3-x11 a mano." >&2
              exit 1
            fi
            """;

        Log.Info("Instalando componentes que faltan…");
        var result = await RootRunner.RunBashAsync(script, TimeSpan.FromMinutes(15), ct).ConfigureAwait(false);
        if (!result.Success)
            Log.Error("La instalación de paquetes falló: " + Trim(result.Output));
        else
            Log.Info("Paquetes instalados.");
        return result;
    }

    private static string Trim(string text)
    {
        const int max = 1800;
        var clean = text.Replace("\r", "");
        return clean.Length <= max ? clean : clean[^max..];
    }
}
