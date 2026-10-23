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

        return store;
    }

    public void Save()
    {
        try
        {
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
}
