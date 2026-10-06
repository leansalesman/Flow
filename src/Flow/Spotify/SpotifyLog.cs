using System.IO;

namespace Flow.Spotify;

/// <summary>
/// Small always-on log of Spotify playback steps and API errors (%LOCALAPPDATA%\Flow\spotify.log).
/// Never contains tokens. Rolls over at ~256 KB.
/// </summary>
public static class SpotifyLog
{
    private static readonly object Lock = new();
    private static readonly string FilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Flow", "spotify.log");

    public static void Write(string message)
    {
        lock (Lock)
        {
            try
            {
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 256 * 1024) File.Move(FilePath, FilePath + ".old", true);
                File.AppendAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}\r\n");
            }
            catch { }
        }
    }
}
