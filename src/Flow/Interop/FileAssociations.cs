using Flow.Library;
using Microsoft.Win32;

namespace Flow.Interop;

/// <summary>
/// Registers Flow (per-user, HKCU only) in the Explorer "Open with" menu for supported audio types.
/// Does not change the user's default apps.
/// </summary>
public static class FileAssociations
{
    private const string ProgId = "Flow.AudioFile";

    public static bool IsRegistered()
    {
        using var k = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId}\shell\open\command");
        var v = k?.GetValue(null) as string;
        return v != null && Environment.ProcessPath != null && v.Contains(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// If "Open with Flow" is registered for a Flow.exe in another folder (Flow was moved), points it at this one.
    /// </summary>
    public static void RefreshIfMoved()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId}\shell\open\command");
            var v = k?.GetValue(null) as string;
            if (v == null || Environment.ProcessPath == null) return;
            if (v.Contains(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return;
            if (!v.Contains("Flow.exe", StringComparison.OrdinalIgnoreCase)) return;
            Register();
        }
        catch { /* best effort */ }
    }

    public static void Register()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path");
        var cmd = $"\"{exe}\" \"%1\"";

        using (var prog = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            prog.SetValue(null, "Audio file (Flow)");
            using (var icon = prog.CreateSubKey("DefaultIcon")) icon.SetValue(null, $"\"{exe}\",0");
            using (var c = prog.CreateSubKey(@"shell\open\command")) c.SetValue(null, cmd);
            using (var o = prog.CreateSubKey(@"shell\open")) o.SetValue("FriendlyAppName", "Flow");
        }

        using (var app = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\Flow.exe"))
        {
            app.SetValue("FriendlyAppName", "Flow");
            using (var c = app.CreateSubKey(@"shell\open\command")) c.SetValue(null, cmd);
            using var types = app.CreateSubKey("SupportedTypes");
            foreach (var ext in AudioFormats.Extensions) types.SetValue(ext, "");
        }

        foreach (var ext in AudioFormats.Extensions)
        {
            using var k = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}\OpenWithProgids");
            k.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        NativeMethods.SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);
    }

    public static void Unregister()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", false); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Applications\Flow.exe", false); } catch { }
        foreach (var ext in AudioFormats.Extensions)
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ext}\OpenWithProgids", true);
                k?.DeleteValue(ProgId, false);
            }
            catch { }
        }
        NativeMethods.SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }
}
