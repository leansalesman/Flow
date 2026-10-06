using System.Runtime.InteropServices;
using System.Windows.Interop;
using static Flow.Interop.NativeMethods;

namespace Flow.Interop;

/// <summary>Minimal notification-area icon with a native context menu.</summary>
public sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = WM_USER + 77;
    private readonly HwndSource _window;
    private readonly int _taskbarCreated;
    private IntPtr _icon;
    private bool _added;
    private string _tip = "Flow";

    public event Action? Activate;
    /// <summary>Asks the host for menu items: (id, text) pairs; id 0 = separator.</summary>
    public Func<IReadOnlyList<(int Id, string Text)>>? MenuProvider { get; set; }
    public event Action<int>? MenuCommand;

    public TrayIcon()
    {
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        var p = new HwndSourceParameters("FlowTrayWindow") { Width = 0, Height = 0, WindowStyle = 0 };
        _window = new HwndSource(p);
        _window.AddHook(WndProc);

        var path = Environment.ProcessPath;
        if (path != null)
        {
            var small = new IntPtr[1];
            if (ExtractIconEx(path, 0, null, small, 1) > 0) _icon = small[0];
        }
    }

    public bool Visible
    {
        get => _added;
        set { if (value) Add(); else Remove(); }
    }

    public string ToolTip
    {
        get => _tip;
        set
        {
            _tip = value.Length > 127 ? value[..127] : value;
            if (_added)
            {
                var d = Data(NIF_TIP | NIF_SHOWTIP);
                Shell_NotifyIcon(NIM_MODIFY, ref d);
            }
        }
    }

    private NOTIFYICONDATA Data(int flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _window.Handle,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = _tip,
        szInfo = "",
        szInfoTitle = "",
    };

    private void Add()
    {
        if (_added) return;
        var d = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        _added = Shell_NotifyIcon(NIM_ADD, ref d);
        if (_added)
        {
            d.uVersion = NOTIFYICON_VERSION_4;
            Shell_NotifyIcon(NIM_SETVERSION, ref d);
        }
    }

    private void Remove()
    {
        if (!_added) return;
        var d = Data(0);
        Shell_NotifyIcon(NIM_DELETE, ref d);
        _added = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _taskbarCreated && _added)
        {
            _added = false;
            Add();
        }
        else if (msg == CallbackMessage)
        {
            int ev = (int)(lParam.ToInt64() & 0xFFFF);
            // NOTIFYICON_VERSION_4: right-click arrives as WM_CONTEXTMENU.
            if (ev == WM_LBUTTONUP) Activate?.Invoke();
            else if (ev == 0x007B /* WM_CONTEXTMENU */) ShowMenu();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        var items = MenuProvider?.Invoke();
        if (items == null || items.Count == 0) return;
        var menu = CreatePopupMenu();
        foreach (var (id, text) in items)
        {
            if (id == 0) AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            else AppendMenu(menu, MF_STRING, (UIntPtr)id, text);
        }
        GetCursorPos(out var pt);
        SetForegroundWindow(_window.Handle);
        int cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN, pt.X, pt.Y, _window.Handle, IntPtr.Zero);
        PostMessage(_window.Handle, 0, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);
        if (cmd != 0) MenuCommand?.Invoke(cmd);
    }

    public void Dispose()
    {
        Remove();
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
        _window.Dispose();
    }
}
