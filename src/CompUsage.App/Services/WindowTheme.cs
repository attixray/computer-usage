using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace CompUsage.App.Services;

public sealed class WindowTheme : IDisposable
{
    private readonly Window window;
    private string mode;
    public WindowTheme(Window window, string mode)
    {
        this.window = window; this.mode = mode;
        window.SourceInitialized += SourceInitialized;
        SystemEvents.UserPreferenceChanged += PreferenceChanged;
    }
    public void Set(string value)
    {
        if (value is not ("auto" or "dark" or "light")) throw new ArgumentException("Érvénytelen nyelv vagy téma.");
        mode = value; Apply();
    }
    private void SourceInitialized(object? sender, EventArgs e) => Apply();
    private void PreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (!window.Dispatcher.HasShutdownStarted) window.Dispatcher.BeginInvoke(Apply);
    }
    private void Apply()
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        var systemDark = key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        ApplyTo(handle, mode == "dark" || mode == "auto" && systemDark, SystemParameters.HighContrast);
    }
    public static int ApplyTo(IntPtr handle, bool dark, bool highContrast = false)
    {
        // Keep the native caption, resize behavior, Snap Layouts and accessibility intact.
        var immersive = dark && !highContrast ? 1 : 0;
        var result = DwmSetWindowAttribute(handle, 20, ref immersive, sizeof(int));
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return result;
        var caption = highContrast ? -1 : dark ? Color(0x222d33) : Color(0xffffff);
        var text = highContrast ? -1 : dark ? Color(0xe9f1f3) : Color(0x20343b);
        var border = highContrast ? -1 : dark ? Color(0x42545d) : Color(0xdbe4e8);
        var captionResult = DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
        var textResult = DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
        var borderResult = DwmSetWindowAttribute(handle, 34, ref border, sizeof(int));
        return result != 0 ? result : captionResult != 0 ? captionResult : textResult != 0 ? textResult : borderResult;
    }
    private static int Color(int rgb) => (rgb & 0xff) << 16 | rgb & 0xff00 | rgb >> 16 & 0xff;
    public void Dispose()
    {
        window.SourceInitialized -= SourceInitialized;
        SystemEvents.UserPreferenceChanged -= PreferenceChanged;
    }
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
