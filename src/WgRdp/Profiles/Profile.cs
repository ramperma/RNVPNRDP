using System.Text.Json.Serialization;

namespace WgRdp.Profiles;

/// <summary>Datos NO confidenciales de un perfil. La contraseña vive en el Administrador de credenciales.</summary>
public sealed class Profile
{
    public string Name { get; set; } = "Predeterminado";

    /// <summary>Nombre del túnel WireGuard (da nombre al archivo .conf y al servicio).</summary>
    public string TunnelName { get; set; } = "wg-rdp";

    /// <summary>Ruta del .conf de origen (por defecto, Setup\wireguard\default.conf junto al exe).</summary>
    public string TunnelConfPath { get; set; } = "";

    /// <summary>Servidor de terminales (IP o nombre) alcanzable por la IP del túnel.</summary>
    public string RdpHost { get; set; } = "";

    public int RdpPort { get; set; } = 3389;

    public string Username { get; set; } = "";
    public string Domain { get; set; } = "";

    /// <summary>IP del servidor por el lado del túnel, para el ping de salud (opcional).</summary>
    public string ServerVpnIp { get; set; } = "";

    /// <summary>0 = none, 1 = server auth, 2 = server + NLA (credssp).</summary>
    public int AuthenticationLevel { get; set; } = 2;

    /// <summary>Profundidad de color del RDP (16 o 24).</summary>
    public int ColorDepth { get; set; } = 16;

    /// <summary>Ancho/alto del cliente RDP (0 = ventana completa).</summary>
    public int RdpWidth { get; set; }
    public int RdpHeight { get; set; }

    /// <summary>Si es true, al terminar la sesión se elimina el servicio de túnel (además de pararlo).</summary>
    public bool RemoveTunnelOnClose { get; set; }

    /// <summary>Redirige las impresoras del equipo local a la sesión remota.</summary>
    public bool RedirectPrinters { get; set; } = true;

    /// <summary>Redirige los discos del equipo local a la sesión remota.</summary>
    public bool RedirectDrives { get; set; } = true;

    /// <summary>Comparte el portapapeles entre el equipo local y la sesión remota.</summary>
    public bool RedirectClipboard { get; set; } = true;

    [JsonIgnore]
    public string ResolvedTunnelConfPath =>
        string.IsNullOrWhiteSpace(TunnelConfPath)
            ? Path.Combine(AppContext.BaseDirectory, "Setup", "wireguard", "default.conf")
            : TunnelConfPath;
}
