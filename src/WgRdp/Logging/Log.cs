namespace WgRdp.Logging;

/// <summary>Logger mínimo a archivo + evento para la UI. Sin dependencias externas.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _logPath;

    public static event Action<LogEntry>? LineWritten;

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WgRdp", "logs");

    public static void Init()
    {
        Directory.CreateDirectory(LogDirectory);
        _logPath = Path.Combine(LogDirectory, $"app-{DateTime.Now:yyyyMMdd-HHmmss}.log");
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    public static void Error(string message, Exception ex)
    {
        WriteToFile("ERROR", $"{message}: {ex}");
        Raise(new LogEntry(DateTime.Now, "ERROR", $"{message}: {ex.Message}"));
    }

    private static void Write(string level, string message)
    {
        WriteToFile(level, message);
        Raise(new LogEntry(DateTime.Now, level, message));
    }

    private static void WriteToFile(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level,-5}] {message}";
        lock (Gate)
        {
            try
            {
                if (_logPath is not null)
                    File.AppendAllText(_logPath, line + Environment.NewLine);
            }
            catch
            {
                // El log nunca debe tumbar la app.
            }
        }
    }

    private static void Raise(LogEntry entry)
    {
        try { LineWritten?.Invoke(entry); }
        catch { /* la UI no debe tumbar el log */ }
    }
}
