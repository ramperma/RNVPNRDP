using System.Text.Json.Serialization;

namespace WgRdp.Profiles;

/// <summary>Datos no confidenciales de un perfil. La contraseña vive fuera de este objeto.</summary>
public sealed class Profile
{
    public string Name { get; set; } = "Predeterminado";

    /// <summary>Nombre de la interfaz WireGuard (también el archivo /etc/wireguard/&lt;nombre&gt;.conf).</summary>
    public string TunnelName { get; set; } = "wg-rdp";

    /// <summary>Ruta del .conf de origen. Vacío usa la copia del asistente.</summary>
    public string TunnelConfPath { get; set; } = "";

    public string RdpHost { get; set; } = "";
    public int RdpPort { get; set; } = 3389;
    public string Username { get; set; } = "";
    public string Domain { get; set; } = "";

    /// <summary>IP del servidor por el lado del túnel, para el ping de salud (opcional).</summary>
    public string ServerVpnIp { get; set; } = "";

    /// <summary>0 = RDP, 1 = TLS, 2 = NLA.</summary>
    public int AuthenticationLevel { get; set; } = 2;

    public int ColorDepth { get; set; } = 32;
    public int RdpWidth { get; set; }
    public int RdpHeight { get; set; }
    public bool RemoveTunnelOnClose { get; set; }
    public bool RedirectPrinters { get; set; } = true;
    public bool RedirectDrives { get; set; } = true;
    public bool RedirectClipboard { get; set; } = true;

    [JsonIgnore]
    public string ResolvedTunnelConfPath
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(TunnelConfPath))
                return TunnelConfPath;

            var bundled = Path.Combine(AppContext.BaseDirectory, "default.conf");
            if (File.Exists(bundled))
                return bundled;

            return Paths.AppPaths.DefaultConfPath;
        }
    }
}
