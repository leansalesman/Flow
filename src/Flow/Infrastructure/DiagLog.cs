using System.IO;

namespace Flow.Infrastructure;

/// <summary>Opt-in diagnostics: set FLOW_DEBUG=1 to write %LOCALAPPDATA%\Flow\debug.log.</summary>
public static class DiagLog
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("FLOW_DEBUG") == "1";
    private static readonly object Lock = new();
    private static readonly string FilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Flow", "debug.log");

    public static void Write(string message)
    {
        if (!Enabled) return;
        lock (Lock)
        {
            try { File.AppendAllText(FilePath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\r\n"); } catch { }
        }
    }
}
