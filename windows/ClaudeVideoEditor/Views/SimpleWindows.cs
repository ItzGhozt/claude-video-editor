using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace ClaudeVideoEditor.Views;

/// Small windows that just host a panel.
public static class SimpleWindows
{
    static readonly Dictionary<string, Window> Open = new();

    /// Shows (or brings to front) a single instance of a window.
    public static Window Show(string id, string title, Func<UIElement> content, double w, double h)
    {
        if (Open.TryGetValue(id, out var existing)) { existing.Activate(); return existing; }
        var win = new Window
        {
            Title = title, Width = w, Height = h, Content = content(),
            Owner = Application.Current.MainWindow, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Icon = Application.Current.MainWindow?.Icon,
        };
        win.Closed += (_, _) => Open.Remove(id);
        Open[id] = win;
        win.Show();
        return win;
    }

    public static void Guide() => Show("guide", "Start Guide", () => new GuidePanel(), 740, 760);

    public static void Settings() => Show("settings", "Settings", () => new TabControl
    {
        Margin = new Thickness(8),
        Items =
        {
            new TabItem { Header = "Account", Content = new SignInPanel() },
            new TabItem { Header = "Setup", Content = new SetupPanel() },
        },
    }, 800, 720);

    public static void Credits() => Show("credits", "Credits", BuildCredits, 620, 600);

    public static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    static UIElement BuildCredits()
    {
        var sp = new StackPanel { Margin = new Thickness(24) };
        TextBlock P(string text, bool secondary = false, double top = 0) =>
            new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 8), Opacity = secondary ? 0.7 : 1 };
        TextBlock H(string text) => new() { Text = text, FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 6) };
        TextBlock Link(string label, string url)
        {
            var h = new Hyperlink(new Run(label)) { NavigateUri = new Uri(url) };
            h.RequestNavigate += (_, e) => { OpenUrl(e.Uri.AbsoluteUri); e.Handled = true; };
            return new TextBlock(h) { Margin = new Thickness(0, 0, 0, 6) };
        }

        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new Image { Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/AppIcon.png")), Width = 60, Height = 60, Margin = new Thickness(0, 0, 14, 0) });
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = "Claude Video Editor", FontSize = 22, FontWeight = FontWeights.SemiBold });
        titles.Children.Add(P("A Windows app for editing video by chatting with Claude. © 2026 Isabel Yeow, MIT License.", true));
        header.Children.Add(titles);
        sp.Children.Add(header);

        sp.Children.Add(H("The editing engine"));
        sp.Children.Add(P("All of the actual video editing (transcription, cutting, color grading, subtitles, animation overlays and self-review of renders) is done by video-use, an open-source Claude Code skill created by Browser Use (originally written by Gregor Žunič). This app is a desktop front end for it and installs it from its official repository."));
        sp.Children.Add(Link("video-use on GitHub", "https://github.com/browser-use/video-use"));
        sp.Children.Add(Link("Browser Use", "https://browser-use.com"));
        sp.Children.Add(P("video-use is released under the MIT License, © 2026 Browser Use.", true));

        sp.Children.Add(H("Also built with"));
        sp.Children.Add(P("• Claude Code by Anthropic, the agent that runs each editing session\n• ffmpeg for rendering\n• ElevenLabs Scribe for transcription (used by video-use)\n• uv by Astral for Python environments\n• Windows speech recognition for the Prompt Creator"));
        sp.Children.Add(P("Claude Video Editor is an independent project and isn't affiliated with or endorsed by Anthropic or Browser Use.", true, 8));
        return new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
