using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CompUsage.App.Services;

public sealed class SingleInstance : IDisposable
{
    private readonly Mutex mutex;
    private readonly EventWaitHandle activation;
    private RegisteredWaitHandle? listener;
    public bool IsPrimary { get; }

    public SingleInstance(string name = "CompUsage")
    {
        // Create the event first so an immediate second launch can queue activation during startup.
        activation = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\" + name + ".Activate");
        mutex = new Mutex(true, @"Local\" + name, out var created);
        IsPrimary = created;
    }

    public void Listen(Action activate)
    {
        if (!IsPrimary || listener is not null) throw new InvalidOperationException("Only the primary instance can listen once.");
        listener = ThreadPool.RegisterWaitForSingleObject(activation, (_, _) => activate(), null, Timeout.Infinite, false);
    }

    public void RequestActivation()
    {
        // The shell-launched process may transfer foreground permission to the existing UI process.
        using var current = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName(current.ProcessName))
        {
            using (process)
            {
                try
                {
                    if (process.Id != current.Id && process.SessionId == current.SessionId
                        && string.Equals(process.MainModule?.FileName, current.MainModule?.FileName, StringComparison.OrdinalIgnoreCase))
                        AllowSetForegroundWindow(process.Id);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        activation.Set();
    }

    public static void BringToFront(Window window)
    {
        if (!window.IsVisible) window.Show();
        // Native restore preserves the previous maximized state of a minimized window.
        var handle = new WindowInteropHelper(window).Handle;
        if (IsIconic(handle)) ShowWindow(handle, 9 /* SW_RESTORE */);
        window.Activate();
        SetForegroundWindow(handle);
    }

    public void Dispose()
    {
        listener?.Unregister(null);
        if (IsPrimary) mutex.ReleaseMutex();
        mutex.Dispose(); activation.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);
}
