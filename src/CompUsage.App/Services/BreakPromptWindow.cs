using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CompUsage.Core;
using Microsoft.Win32;

namespace CompUsage.App.Services;

// Small bottom-right question after a long break. A choice saves and closes at once; the countdown waits while the user is here.
public sealed class BreakPromptWindow : Window
{
    private readonly string suggestion;
    private readonly Action<string, string, bool> save;
    private readonly bool english;
    private readonly TimeSpan total;
    private readonly DispatcherTimer timer;
    private readonly TextBlock countdown = new() { FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
    private readonly Border bar = new() { Height = 3, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(2) };
    private readonly Border track = new() { Height = 3, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 6, 0, 0) };
    private readonly StackPanel otherRow = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBox otherText = new() { MaxLength = BreakReasons.MaximumText, Padding = new Thickness(6, 4, 6, 4) };
    private readonly Brush ink, muted, paper, line, brand, buttonInk, soft;
    private TimeSpan remaining;
    private DateTime lastTick = DateTime.UtcNow;
    private bool done;

    public BreakPromptWindow(DateTimeOffset from, DateTimeOffset to, string suggestion, AppSettings settings, Action<string, string, bool> save)
    {
        this.suggestion = suggestion; this.save = save;
        english = settings.Language == "en";
        total = remaining = TimeSpan.FromSeconds(settings.BreakPromptSeconds);
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
        {
            var dark = settings.Theme == "dark" || settings.Theme == "auto" && key?.GetValue("AppsUseLightTheme") is int light && light == 0;
            Brush Pick(int lightColor, int darkColor) => Frozen(dark ? darkColor : lightColor);
            ink = Pick(0x20343b, 0xe9f1f3); muted = Pick(0x576c75, 0xb2c4cb); paper = Pick(0xffffff, 0x222d33);
            line = Pick(0xdbe4e8, 0x42545d); brand = Pick(0x146b66, 0x9aded6); buttonInk = Pick(0xffffff, 0x133b38); soft = Pick(0xedf3f4, 0x29383f);
        }
        Title = "CompUsage"; WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize; Topmost = true; ShowInTaskbar = false; ShowActivated = false;
        Width = 330; SizeToContent = SizeToContent.Height; FontFamily = new FontFamily("Segoe UI"); FontSize = 13; Foreground = ink;

        var minutes = (int)Math.Round((to - from).TotalMinutes);
        var range = $"{TimeZoneInfo.ConvertTime(from, TimeZoneInfo.Local):H:mm} – {TimeZoneInfo.ConvertTime(to, TimeZoneInfo.Local):H:mm} · {minutes} {(english ? "min" : "perc")}";
        var close = Plain("✕", english ? "Skip" : "Kihagyás");
        close.Click += (_, _) => Finish(null, "", false);
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(new TextBlock { Text = english ? "What was this break?" : "Mi volt a szünet oka?", FontWeight = FontWeights.SemiBold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        var content = new StackPanel();
        content.Children.Add(header);
        content.Children.Add(new TextBlock { Text = range, Foreground = muted, FontSize = 12, Margin = new Thickness(0, 0, 0, 8) });
        foreach (var reason in BreakReasons.Codes) content.Children.Add(Choice(reason));
        var saveOther = Plain(english ? "Save" : "Mentés", null);
        saveOther.Background = brand; saveOther.Foreground = buttonInk; saveOther.BorderBrush = brand; saveOther.Margin = new Thickness(6, 0, 0, 0);
        saveOther.Click += (_, _) => Finish(BreakReasons.Other, otherText.Text, false);
        otherText.Background = paper; otherText.Foreground = ink; otherText.BorderBrush = line; otherText.CaretBrush = ink;
        otherText.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Finish(BreakReasons.Other, otherText.Text, false); } };
        var otherLine = new DockPanel();
        DockPanel.SetDock(saveOther, Dock.Right);
        otherLine.Children.Add(saveOther); otherLine.Children.Add(otherText);
        otherRow.Children.Add(new TextBlock { Text = english ? "Description (optional)" : "Leírás (nem kötelező)", Foreground = muted, FontSize = 11.5, Margin = new Thickness(0, 0, 0, 3) });
        otherRow.Children.Add(otherLine);
        content.Children.Add(otherRow);
        countdown.Foreground = muted; countdown.Margin = new Thickness(0, 10, 0, 0);
        track.Background = soft; bar.Background = brand; track.Child = bar;
        content.Children.Add(countdown); content.Children.Add(track);
        Content = new Border { Background = paper, BorderBrush = line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 10, 14, 12), Margin = new Thickness(10), Child = content,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.28 } };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Finish(null, "", false); };
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 6; Top = area.Bottom - ActualHeight - 6;
        };
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Normal, (_, _) => Tick(), Dispatcher);
        Closed += (_, _) => timer.Stop();
        Update(false);
    }

    private Button Choice(string reason)
    {
        var suggested = reason == suggestion;
        var name = ReasonName(reason);
        var note = reason == BreakReasons.Other ? "" : BreakReasons.CountsAsWork(reason) ? (english ? "work time" : "munkaidő") : (english ? "break" : "szünet");
        var row = new DockPanel();
        var right = new TextBlock { Text = suggested ? (english ? "suggested · " : "javasolt · ") + note : note, FontSize = 11.5, Opacity = 0.85, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(right, Dock.Right);
        row.Children.Add(right);
        row.Children.Add(new TextBlock { Text = name, FontWeight = suggested ? FontWeights.SemiBold : FontWeights.Normal });
        var button = Plain(null, null);
        button.Content = row; button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.Margin = new Thickness(0, 0, 0, 4);
        button.Padding = new Thickness(10, 6, 10, 6);
        if (suggested) { button.Background = brand; button.Foreground = buttonInk; button.BorderBrush = brand; }
        button.Click += (_, _) =>
        {
            if (reason != BreakReasons.Other) { Finish(reason, "", false); return; }
            otherRow.Visibility = Visibility.Visible;
            Activate(); otherText.Focus();
        };
        return button;
    }

    private string ReasonName(string reason) => reason switch
    {
        BreakReasons.Standup => english ? "Stand-up meeting" : "Standup meeting",
        BreakReasons.Lunch => english ? "Lunch break" : "Ebédszünet",
        BreakReasons.Coffee => english ? "Coffee break" : "Kávészünet",
        BreakReasons.Meeting => "Meeting",
        _ => english ? "Other break…" : "Egyéb szünet…"
    };

    private Button Plain(string? text, string? tip)
    {
        var button = new Button { Content = text, ToolTip = tip, Background = soft, Foreground = ink, BorderBrush = line, BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 3, 8, 3), Cursor = Cursors.Hand, FocusVisualStyle = null };
        // A flat template keeps the brand colors on hover instead of the default blue chrome.
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetBinding(ContentPresenter.HorizontalAlignmentProperty, new System.Windows.Data.Binding("HorizontalContentAlignment") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.88));
        template.Triggers.Add(hover);
        button.Template = template;
        return button;
    }

    // While the pointer is over the window, it has focus or the user is typing, the prompt stays.
    private bool Paused => IsMouseOver || IsActive || IsKeyboardFocusWithin;

    public void Start() { Show(); timer.Start(); }

    private void Tick()
    {
        var now = DateTime.UtcNow;
        var paused = Paused;
        if (!paused) remaining -= now - lastTick;
        lastTick = now;
        if (remaining <= TimeSpan.Zero)
        {
            if (BreakReasons.SavedOnTimeout(suggestion)) Finish(suggestion, "", true); else Finish(null, "", false);
            return;
        }
        Update(paused);
    }

    private void Update(bool paused)
    {
        var seconds = (int)Math.Ceiling(Math.Max(0, remaining.TotalSeconds));
        var outcome = BreakReasons.SavedOnTimeout(suggestion)
            ? (english ? $"then “{ReasonName(suggestion)}” is saved" : $"utána a(z) „{ReasonName(suggestion)}” mentődik")
            : (english ? "nothing is saved" : "semmi nem mentődik");
        countdown.Text = paused
            ? (english ? $"Paused while you are here · {seconds} s left" : $"Szünetel, amíg itt vagy · még {seconds} mp")
            : (english ? $"Closes in {seconds} s · {outcome}" : $"Bezárul {seconds} mp múlva · {outcome}");
        bar.Width = Math.Max(0, (Width - 50) * remaining.TotalMilliseconds / total.TotalMilliseconds);
    }

    private void Finish(string? reason, string text, bool automatic)
    {
        if (done) return;
        done = true; timer.Stop();
        try { if (reason is not null) save(reason, text, automatic); }
        finally { Close(); }
    }

    private static SolidColorBrush Frozen(int rgb)
    {
        var brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        brush.Freeze();
        return brush;
    }
}
