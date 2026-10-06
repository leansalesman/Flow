using System.IO;
using System.Text;
using Flow.Library;

namespace Flow.Services;

/// <summary>M3U / M3U8 import and export.</summary>
public static class PlaylistFile
{
    public static List<string> Read(string file)
    {
        var result = new List<string>();
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(file)) ?? "";
        IEnumerable<string> lines;
        try { lines = File.ReadAllLines(file, Encoding.UTF8); }
        catch { return result; }
        foreach (var raw in lines)
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            string path;
            if (Uri.TryCreate(line, UriKind.Absolute, out var uri) && uri.IsFile) path = uri.LocalPath;
            else path = Path.IsPathRooted(line) ? line : Path.GetFullPath(Path.Combine(baseDir, line));
            if (File.Exists(path) && AudioFormats.IsSupported(path)) result.Add(path);
        }
        return result;
    }

    public static void Write(string file, IEnumerable<Track> tracks)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");
        foreach (var t in tracks)
        {
            sb.AppendLine($"#EXTINF:{(int)t.Duration.TotalSeconds},{t.DisplayArtist} - {t.Title}");
            sb.AppendLine(t.Path);
        }
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
    }
}
