using System.Text.Json;
using WgRdp.Logging;

namespace WgRdp.Setup;

/// <summary>Marca si el asistente de primera ejecución ya se completó.</summary>
public sealed class SetupState
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WgRdp");

    private static readonly string FilePath = Path.Combine(DataDir, "setup.json");

    public bool FirstRunCompleted { get; set; }

    public static SetupState Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<SetupState>(File.ReadAllText(FilePath)) ?? new SetupState();
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo leer el estado de configuración", ex);
        }
        return new SetupState();
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this,
            new JsonSerializerOptions { WriteIndented = true }));
    }
}
