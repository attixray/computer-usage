using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace CompUsage.App.Services;

// A notification-area icon on an existing window, without pulling Windows Forms into the WPF app.
public sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = 0x8000 + 0x43, OpenCommand = 1, ExitCommand = 2, FirstEntry = 100;
    private readonly HwndSource source;
    private readonly Func<(string Tip, string Open, string Exit)> labels;
    private readonly Action open, exit;
    private readonly Func<IReadOnlyList<(string Text, Action? Run)>>? entries;
    private string? tip;
    private readonly int taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private readonly IntPtr icon;
    private bool added;

    // Entries are listed under Open; one without an action is shown greyed out, as a heading or note.
    public TrayIcon(HwndSource source, Func<(string Tip, string Open, string Exit)> labels, Action open, Action exit,
        Func<IReadOnlyList<(string Text, Action? Run)>>? entries = null)
    {
        this.source = source; this.labels = labels; this.open = open; this.exit = exit; this.entries = entries;
        ExtractIconEx(Environment.ProcessPath!, 0, out var large, out icon, 1);
        if (large != IntPtr.Zero) DestroyIcon(large);
        source.AddHook(Messages);
        Add();
    }

    private void Add()
    {
        var data = Data();
        data.Flags = 1 | 2 | 4; data.CallbackMessage = CallbackMessage; data.Icon = icon; data.Tip = Tip();
        added = Shell_NotifyIcon(0, ref data) || Shell_NotifyIcon(1, ref data);
    }

    private string Tip() { var text = tip ?? labels().Tip; return text.Length > 127 ? text[..127] : text; }

    public void SetTip(string? text)
    {
        if (tip == text) return;
        tip = text;
        if (!added) return;
        var data = Data(); data.Flags = 4; data.Tip = Tip();
        Shell_NotifyIcon(1, ref data);
    }

    // A one-off Windows notification from the icon.
    public void Notify(string title, string text)
    {
        if (!added) return;
        var data = Data(); data.Flags = 0x10; data.InfoTitle = title.Length > 63 ? title[..63] : title;
        data.Info = text.Length > 255 ? text[..255] : text; data.InfoFlags = 1;
        Shell_NotifyIcon(1, ref data);
    }

    private NotifyIconData Data() => new() { Size = Marshal.SizeOf<NotifyIconData>(), Window = source.Handle, Id = 1, Tip = "", Info = "", InfoTitle = "" };

    private IntPtr Messages(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Explorer restarts drop every icon; add it again when the new taskbar announces itself.
        if (message == taskbarCreated) { Add(); return IntPtr.Zero; }
        if (message != CallbackMessage) return IntPtr.Zero;
        handled = true;
        switch (lParam.ToInt32())
        {
            case 0x0202: open(); break;
            case 0x0205: Menu(hwnd); break;
        }
        return IntPtr.Zero;
    }

    private void Menu(IntPtr hwnd)
    {
        var text = labels();
        var listed = entries?.Invoke() ?? [];
        var menu = CreatePopupMenu();
        try
        {
            AppendMenu(menu, 0, OpenCommand, text.Open);
            if (listed.Count > 0) AppendMenu(menu, 0x800, 0, null);
            for (var i = 0; i < listed.Count; i++) AppendMenu(menu, listed[i].Run is null ? 0x1 : 0, FirstEntry + i, listed[i].Text);
            AppendMenu(menu, 0x800, 0, null);
            AppendMenu(menu, 0, ExitCommand, text.Exit);
            SetMenuDefaultItem(menu, OpenCommand, 0);
            GetCursorPos(out var point);
            // Without the foreground window the menu would not close when the user clicks elsewhere.
            SetForegroundWindow(hwnd);
            var command = TrackPopupMenuEx(menu, 0x0100 | 0x0002, point.X, point.Y, hwnd, IntPtr.Zero);
            PostMessage(hwnd, 0, IntPtr.Zero, IntPtr.Zero);
            if (command == OpenCommand) open();
            else if (command == ExitCommand) exit();
            else if (command >= FirstEntry && command - FirstEntry < listed.Count) listed[command - FirstEntry].Run?.Invoke();
        }
        finally { DestroyMenu(menu); }
    }

    public void Dispose()
    {
        source.RemoveHook(Messages);
        if (added) { var data = Data(); Shell_NotifyIcon(2, ref data); added = false; }
        if (icon != IntPtr.Zero) DestroyIcon(icon);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int Size; public IntPtr Window; public int Id; public int Flags; public int CallbackMessage; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public int State; public int StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public int Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public int InfoFlags; public Guid Item; public IntPtr BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);
    [DllImport("shell32.dll", EntryPoint = "ExtractIconExW", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint icons);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string name);
    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(IntPtr menu, int flags, int id, string? text);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetMenuDefaultItem(IntPtr menu, int item, int byPosition);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, int flags, int x, int y, IntPtr window, IntPtr parameters);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
