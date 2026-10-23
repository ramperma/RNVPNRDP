using System.Text;

namespace WgRdp.Linux;

public sealed record ToolStatus(
    bool WireGuard, bool FreeRdp, bool SecretTool, string? FreeRdpPath, string? RemminaPath)
{
    public string Describe()
    {
        var missing = new List<string>();
        if (!WireGuard)
            missing.Add("WireGuard (wireguard-tools)");
        if (!FreeRdp)
            missing.Add("un cliente RDP (Remmina o xfreerdp)");
        if (missing.Count == 0 && RemminaPath is not null && FreeRdpPath is null && !SecretTool)
            return "WireGuard y Remmina están instalados (Remmina usa FreeRDP). Sin secret-tool, la contraseña se guarda en un archivo privado.";
        if (missing.Count == 0 && RemminaPath is not null && FreeRdpPath is null)
            return "WireGuard y Remmina están instalados. El escritorio se abrirá con Remmina, que usa FreeRDP.";
        if (missing.Count == 0 && !SecretTool)
            return "WireGuard y FreeRDP están instalados. Sin secret-tool, la contraseña se guarda en un archivo privado.";
        if (missing.Count == 0)
            return "WireGuard y FreeRDP están instalados.";
        return "Falta: " + string.Join(", ", missing) + ".";
    }
}

/// <summary>Localiza wg, wg-quick, FreeRDP y secret-tool en el PATH.</summary>
public static class LinuxTools
{
    private static readonly string[] FreeRdpNames = ["xfreerdp", "xfreerdp3", "sdl-freerdp", "wlfreerdp"];

    public static ToolStatus Detect()
    {
        var free = FindFreeRdp();
        var remmina = FindRemminaRdp();
        return new ToolStatus(
            Which("wg") is not null && Which("wg-quick") is not null,
            free is not null || remmina is not null,
            Which("secret-tool") is not null,
            free,
            remmina);
    }

    /// <summary>Remmina con el complemento RDP, que enlaza libfreerdp.</summary>
    public static string? FindRemminaRdp()
    {
        var remmina = Which("remmina");
        if (remmina is null || !RemminaRdpPluginExists())
            return null;
        return remmina;
    }

    private static bool RemminaRdpPluginExists()
    {
        var roots = new List<string> { "/usr/lib", "/usr/lib64" };
        try
        {
            if (Directory.Exists("/usr/lib"))
                roots.AddRange(Directory.GetDirectories("/usr/lib"));
        }
        catch
        {
            // Si no se puede listar /usr/lib, se prueban las rutas fijas.
        }

        foreach (var root in roots)
        {
            var plugin = Path.Combine(root, "remmina", "plugins", "remmina-plugin-rdp.so");
            if (File.Exists(plugin))
                return true;
        }

        return false;
    }

    public static string? FindFreeRdp()
    {
        string? wayland = null;
        string? any = null;
        foreach (var name in FreeRdpNames)
        {
            var path = Which(name);
            if (path is null)
                continue;
            any ??= path;
            if (name == "wlfreerdp")
                wayland = path;
        }

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) && wayland is not null)
            return wayland;
        return any;
    }

    public static string? Which(string name)
    {
        if (name.Contains('/') || name.Contains('\0'))
            return null;

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        var dirs = pathEnv.Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Concat(["/usr/bin", "/usr/sbin", "/bin", "/sbin"]);

        foreach (var dir in dirs)
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    public static string Report()
    {
        var status = Detect();
        var sb = new StringBuilder();
        sb.AppendLine("WG-RDP Linux");
        sb.AppendLine("wg:          " + (Which("wg") ?? "no encontrado"));
        sb.AppendLine("wg-quick:    " + (Which("wg-quick") ?? "no encontrado"));
        sb.AppendLine("xfreerdp:    " + (status.FreeRdpPath ?? "no encontrado"));
        sb.AppendLine("Remmina:     " + (status.RemminaPath ?? "no encontrado"));
        sb.AppendLine("secret-tool: " + (Which("secret-tool") ?? "no encontrado"));
        sb.AppendLine("pkexec:      " + (Which("pkexec") ?? "no encontrado"));
        sb.AppendLine(status.Describe());
        return sb.ToString().TrimEnd();
    }
}
