using System.Windows;
using CompUsage.App.Services;

namespace CompUsage.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--verify-install"])
        {
            try
            {
                _ = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString();
                return new[] { "index.html", "app.js", "app.css", "desktop.css", "i18n.js", "vendor/lucide.min.js" }
                    .All(file => File.Exists(Path.Combine(AppContext.BaseDirectory, "Ui", file))) ? 0 : 2;
            }
            catch { return 3; }
        }
        if (args.Length == 3 && args[0] == "--broker" && int.TryParse(args[2], out var parent))
        {
            try { SecurityBroker.RunAsync(args[1], parent).GetAwaiter().GetResult(); return 0; }
            catch { return 1; }
        }
        using var mutex = new Mutex(true, @"Local\CompUsage", out var created);
        if (!created) { MessageBox.Show("A CompUsage már fut.", "CompUsage"); return 0; }
        try
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            return app.Run(new MainWindow());
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "CompUsage", MessageBoxButton.OK, MessageBoxImage.Error); return 1; }
    }
}
