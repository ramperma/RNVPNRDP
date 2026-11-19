using System.Text.Json;
using System.Text.Json.Serialization;
using WgRdp.Logging;
using WgRdp.Paths;

namespace WgRdp.Profiles;

/// <summary>Perfiles en $XDG_CONFIG_HOME/wgrdp/profiles.json, sin secretos.</summary>
public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public List<Profile> Profiles { get; private set; } = [];

    public static ProfileStore Load()
    {
        var store = new ProfileStore();
        try
        {
            if (File.Exists(AppPaths.ProfilesPath))
            {
                var list = JsonSerializer.Deserialize<List<Profile>>(
                    File.ReadAllText(AppPaths.ProfilesPath), JsonOpts);
                if (list is not null)
                    store.Profiles = list;
            }
        }
        catch (Exception ex)
        {
            Log.Error("No se pudieron leer los perfiles", ex);
        }

        if (store.Profiles.Count == 0)
            store.Profiles.Add(new Profile());

        foreach (var profile in store.Profiles)
            profile.EnsureId();

        return store;
    }

    public void Save()
    {
        try
        {
            foreach (var profile in Profiles)
                profile.EnsureId();
            AppPaths.Ensure();
            File.WriteAllText(AppPaths.ProfilesPath, JsonSerializer.Serialize(Profiles, JsonOpts));
            AppPaths.TryPrivate(AppPaths.ProfilesPath);
        }
        catch (Exception ex)
        {
            Log.Error("No se pudieron guardar los perfiles", ex);
            throw;
        }
    }

    public Profile? Find(string name) =>
        Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public Profile? FindById(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : Profiles.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    public Profile? FindByNameExcluding(string name, string? excludeId) =>
        Profiles.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(p.Id, excludeId, StringComparison.OrdinalIgnoreCase));

    public string UniqueName(string baseName)
    {
        var trimmed = string.IsNullOrWhiteSpace(baseName) ? "Perfil" : baseName.Trim();
        if (Find(trimmed) is null)
            return trimmed;
        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{trimmed} {i}";
            if (Find(candidate) is null)
                return candidate;
        }

        return $"{trimmed} {Guid.NewGuid().ToString("N")[..4]}";
    }

    public Profile CreateNew(string baseName = "Nuevo perfil")
    {
        var profile = new Profile
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = UniqueName(baseName),
            TunnelName = "wg-rdp",
            TunnelConfPath = "",
            TunnelConfLabel = "",
        };
        Profiles.Add(profile);
        return profile;
    }

    public static bool LooksLikeWireGuard(string text) =>
        text.Contains("[Interface]", StringComparison.OrdinalIgnoreCase)
        && text.Contains("[Peer]", StringComparison.OrdinalIgnoreCase);

    public static string SaveProfileConf(Profile profile, string content, string label)
    {
        var trimmed = content.Trim();
        if (!LooksLikeWireGuard(trimmed))
            throw new InvalidOperationException(
                "El archivo no parece una configuración WireGuard (falta [Interface] o [Peer]).");

        var id = profile.EnsureId();
        AppPaths.Ensure();
        var dest = AppPaths.ProfileConfPath(id);
        File.WriteAllText(dest, trimmed + Environment.NewLine);
        AppPaths.TryPrivate(dest);

        profile.TunnelConfPath = dest;
        profile.TunnelConfLabel = string.IsNullOrWhiteSpace(label) ? Path.GetFileName(dest) : label.Trim();
        return dest;
    }

    public static void DeleteManagedConf(Profile profile)
    {
        if (!AppPaths.IsManagedProfileConfPath(profile.TunnelConfPath))
            return;
        try
        {
            if (File.Exists(profile.TunnelConfPath))
                File.Delete(profile.TunnelConfPath);
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo borrar el .conf del perfil: " + ex.Message);
        }
    }
}
