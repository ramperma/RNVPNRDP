using System.Text.Json;
using WgRdp.Logging;
using WgRdp.Paths;

namespace WgRdp.Setup;

public sealed class SetupState
{
    public bool FirstRunCompleted { get; set; }

    public static SetupState Load()
    {
        try
        {
            if (File.Exists(AppPaths.SetupPath))
                return JsonSerializer.Deserialize<SetupState>(File.ReadAllText(AppPaths.SetupPath))
                       ?? new SetupState();
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo leer el estado de configuración", ex);
        }

        return new SetupState();
    }

    public void Save()
    {
        AppPaths.Ensure();
        File.WriteAllText(AppPaths.SetupPath, JsonSerializer.Serialize(this,
            new JsonSerializerOptions { WriteIndented = true }));
        AppPaths.TryPrivate(AppPaths.SetupPath);
    }
}
