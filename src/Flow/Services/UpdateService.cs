using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Flow.Infrastructure;

namespace Flow.Services;

/// <summary>A newer Flow on GitHub Releases.</summary>
public sealed record FlowUpdate(Version Version, string SetupUrl, string SetupName, long Size, string? Sha256, string NotesUrl);

/// <summary>
/// Checks GitHub Releases for a newer Flow and installs it: downloads Flow-Setup-x64, verifies it against the
/// SHA-256 digest GitHub publishes for the file, runs it silently (it closes and updates Flow) and has the setup
/// reopen Flow. Free: the public GitHub API needs no account or key.
/// </summary>
public sealed class UpdateService
{
    private const string LatestReleaseApi = "https://api.github.com/repos/leansalesman/Flow/releases/latest";

    // Declared before Http: static fields initialize in order, and the client's user agent needs the version.
    public static Version CurrentVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(1, 0, 0);

    private static readonly Lazy<HttpClient> HttpLazy = new(CreateClient);
    private static HttpClient Http => HttpLazy.Value;

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // GitHub's API requires a user agent; TryParseAdd never throws.
        c.DefaultRequestHeaders.UserAgent.TryParseAdd($"Flow/{CurrentVersion}");
        c.DefaultRequestHeaders.Accept.TryParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>The newer release, or null when Flow is up to date. Throws when GitHub can't be reached.</summary>
    /// <param name="force">Return the latest release even when it isn't newer (developer test).</param>
    public async Task<FlowUpdate?> CheckAsync(bool force = false)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var json = JsonNode.Parse(await Http.GetStringAsync(LatestReleaseApi, cts.Token))!;
        var tag = json["tag_name"]?.GetValue<string>()?.TrimStart('v', 'V') ?? "";
        if (!Version.TryParse(tag, out var latest)) return null;
        latest = new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build));
        if (latest <= CurrentVersion && !force) return null;

        var asset = (json["assets"] as JsonArray)?.FirstOrDefault(a =>
            a?["name"]?.GetValue<string>() is { } n && n.StartsWith("Flow-Setup-x64", StringComparison.OrdinalIgnoreCase)
                                                    && n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        var notes = json["html_url"]?.GetValue<string>() ?? "https://github.com/leansalesman/Flow/releases/latest";
        if (asset == null) return new FlowUpdate(latest, "", "", 0, null, notes);   // no setup attached: notes only
        var digest = asset["digest"]?.GetValue<string>();
        return new FlowUpdate(latest,
            asset["browser_download_url"]!.GetValue<string>(),
            asset["name"]!.GetValue<string>(),
            asset["size"]?.GetValue<long>() ?? 0,
            digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : null,
            notes);
    }

    /// <summary>Downloads the setup (progress 0..1) and verifies its SHA-256. Returns the file path.</summary>
    public async Task<string> DownloadAsync(FlowUpdate update, IProgress<double>? progress)
    {
        if (string.IsNullOrEmpty(update.SetupUrl)) throw new InvalidOperationException("This release has no setup to install.");
        if (update.Sha256 == null) throw new InvalidOperationException("GitHub didn't provide a checksum for the setup, so it can't be verified.");
        var dir = Path.Combine(Path.GetTempPath(), "FlowUpdate");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, update.SetupName);

        using (var resp = await Http.GetAsync(update.SetupUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? update.Size;
            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var dst = File.Create(path);
            var buffer = new byte[81920];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buffer)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n));
                done += n;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        string actual;
        await using (var f = File.OpenRead(path)) actual = Convert.ToHexString(await SHA256.HashDataAsync(f)).ToLowerInvariant();
        if (actual != update.Sha256)
        {
            try { File.Delete(path); } catch { }
            throw new InvalidOperationException("The download didn't match GitHub's checksum, so it wasn't installed.");
        }
        DiagLog.Write($"Update {update.Version} downloaded and verified");
        return path;
    }

    /// <summary>
    /// Starts the verified setup silently. It closes Flow (Flow is asked to exit right after this), installs the
    /// update in place and reopens Flow; resume = start playing again where it left off.
    /// </summary>
    public static void StartInstall(string setupPath, bool resume)
    {
        var args = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RELAUNCH=1" + (resume ? " /RESUME=1" : "");
        Process.Start(new ProcessStartInfo(setupPath, args) { UseShellExecute = false });
    }
}
