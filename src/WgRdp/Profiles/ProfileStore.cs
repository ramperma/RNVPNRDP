using System.Text.Json;
using System.Text.Json.Serialization;
using WgRdp.Logging;

namespace WgRdp.Profiles;

/// <summary>Guarda los perfiles en %ProgramData%\WgRdp\profiles.json (sin secretos).</summary>
public sealed class ProfileStore
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WgRdp");

    private static readonly string FilePath = Path.Combine(DataDir, "profiles.json");

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
            if (File.Exists(FilePath))
            {
                var list = JsonSerializer.Deserialize<List<Profile>>(File.ReadAllText(FilePath), JsonOpts);
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
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Profiles, JsonOpts));
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
