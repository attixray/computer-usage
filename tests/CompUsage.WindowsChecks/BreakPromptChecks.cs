using System.Windows;
using System.Windows.Controls;
using CompUsage.App.Services;
using CompUsage.Core;

internal static class BreakPromptChecks
{
    private static IEnumerable<DependencyObject> Elements(DependencyObject parent)
    {
        yield return parent;
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
            foreach (var element in Elements(child)) yield return element;
    }

    private static T Find<T>(BreakPromptWindow window, string name) where T : FrameworkElement =>
        Elements(window).OfType<T>().Single(e => e.Name == name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }

    // Exercise real WPF controls without showing a popup or writing a user's journal.
    private static void Prompt(AppSettings settings, string reason,
        Action<BreakPromptWindow, List<(string Reason, string Text, bool Automatic)>, Func<bool>> verify,
        bool reopened = false, string? currentReason = null, string currentText = "")
    {
        var answers = new List<(string Reason, string Text, bool Automatic)>();
        var start = new DateTimeOffset(2026, 10, 19, 14, 0, 0, TimeSpan.FromHours(2));
        var window = new BreakPromptWindow(start, start.AddMinutes(30), reason, settings,
            (code, text, automatic) => answers.Add((code, text, automatic)), reopened, currentReason, currentText);
        var closed = false;
        window.Closed += (_, _) => closed = true;
        try { verify(window, answers, () => closed); }
        finally { if (!closed) window.Close(); }
    }

    public static void Run(Action<string, Action> check)
    {
        check("Other work and other break save once and close immediately with description fields off by default", () =>
        {
            var settings = new AppSettings();
            Require(!settings.PromptForWorkDescription && !settings.PromptForBreakDescription, "Descriptions were enabled by default");
            foreach (var language in new[] { "hu", "en" })
            foreach (var reason in new[] { BreakReasons.Work, BreakReasons.Other })
                Prompt(settings with { Language = language }, reason, (window, answers, closed) =>
                {
                    Require(!Elements(window).OfType<TextBox>().Any(), "Disabled description fields still exist");
                    var button = Find<Button>(window, "Reason_" + reason);
                    Require(Elements(button).OfType<TextBlock>().All(t => !t.Text.EndsWith('…')), "An immediate choice advertised another step");
                    Click(button);
                    Require(closed() && answers.SequenceEqual([(reason, "", false)]), "One click did not save and close");
                    Click(button);
                    Require(answers.Count == 1, "A second click saved twice");
                });
        });
        check("Description switches work independently and enabled editors retain Save and quick choices", () =>
        {
            foreach (var reason in new[] { BreakReasons.Work, BreakReasons.Other })
            {
                var settings = new AppSettings
                {
                    PromptForWorkDescription = reason == BreakReasons.Work,
                    PromptForBreakDescription = reason == BreakReasons.Other,
                    WorkSuggestions = ["Design review"], BreakSuggestions = ["Appointment"]
                };
                var other = reason == BreakReasons.Work ? BreakReasons.Other : BreakReasons.Work;
                Prompt(settings, reason, (window, answers, closed) =>
                {
                    Require(Elements(window).OfType<TextBox>().Single().Name == "Description_" + reason, "The switches were not independent");
                    Click(Find<Button>(window, "Reason_" + reason));
                    Require(!closed() && answers.Count == 0, "An enabled editor saved before confirmation");
                    Find<TextBox>(window, "Description_" + reason).Text = "Árvíztűrő review";
                    Click(Find<Button>(window, "SaveDescription_" + reason));
                    Require(closed() && answers.SequenceEqual([(reason, "Árvíztűrő review", false)]), "Save lost the chosen reason or text");
                });
                Prompt(settings, reason, (window, answers, closed) =>
                {
                    Click(Find<Button>(window, "Reason_" + reason));
                    var text = reason == BreakReasons.Work ? "Design review" : "Appointment";
                    Click(Elements(window).OfType<Button>().Single(b => b.Content as string == text));
                    Require(closed() && answers.SequenceEqual([(reason, text, false)]), "A quick choice failed to save immediately");
                });
                Prompt(settings, other, (window, answers, closed) =>
                {
                    Click(Find<Button>(window, "Reason_" + other));
                    Require(closed() && answers.SequenceEqual([(other, "", false)]), "The disabled reason still required confirmation");
                });
            }
        });
        check("Reopening a saved reason with its editor disabled does not save on load and preserves its text on confirmation", () =>
        {
            foreach (var reason in new[] { BreakReasons.Work, BreakReasons.Other })
            {
                Prompt(new(), reason, (window, answers, closed) =>
                {
                    window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                    Require(!closed() && answers.Count == 0, "Reopening saved without a user choice");
                    Click(Find<Button>(window, "Reason_" + reason));
                    Require(closed() && answers.SequenceEqual([(reason, "Saved description", false)]), "One-click reconfirmation erased the saved text");
                }, reopened: true, currentReason: reason, currentText: "Saved description");
                var other = reason == BreakReasons.Work ? BreakReasons.Other : BreakReasons.Work;
                Prompt(new(), reason, (window, answers, closed) =>
                {
                    Click(Find<Button>(window, "Reason_" + other));
                    Require(closed() && answers.SequenceEqual([(other, "", false)]), "Text leaked into a different reason");
                }, reopened: true, currentReason: reason, currentText: "Saved description");
            }
        });
    }
}
