using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClaudeVideoEditor.Services;

namespace ClaudeVideoEditor.Views;

/// First-run flow: Welcome -> Sign in -> Set up -> Start guide.
public partial class OnboardingView : UserControl
{
    static readonly string[] Titles = { "Welcome", "Sign in", "Set up", "Start guide" };
    int _step;
    public event Action? Finished;

    public OnboardingView(int step = 0)
    {
        InitializeComponent();
        _step = Math.Clamp(step, 0, Titles.Length - 1);
        System.ComponentModel.PropertyChangedEventHandler onAuth = (_, _) => Dispatcher.BeginInvoke(UpdateFooter);
        Action onSetup = () => Dispatcher.BeginInvoke(UpdateFooter);
        Loaded += async (_, _) =>
        {
            AuthManager.Shared.PropertyChanged += onAuth;
            SetupManager.Shared.Changed += onSetup;
            await AuthManager.Shared.RefreshAsync();
            await SetupManager.Shared.RefreshAsync();
        };
        Unloaded += (_, _) =>
        {
            AuthManager.Shared.PropertyChanged -= onAuth;
            SetupManager.Shared.Changed -= onSetup;
        };
        Show();
    }

    public int Step { get => _step; set { _step = value; Show(); } }

    void Show()
    {
        Page.Content = _step switch
        {
            0 => new WelcomePage(),
            1 => new SignInPanel(),
            2 => new SetupPanel(),
            _ => new GuidePanel(),
        };
        Steps.Children.Clear();
        for (int i = 0; i < Titles.Length; i++)
        {
            var done = i < _step;
            var dot = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 6, 0),
                Background = i <= _step ? (Brush)FindResource("Accent") : (Brush)FindResource("Subtle"),
                Child = new TextBlock
                {
                    Text = done ? "" : (i + 1).ToString(),
                    FontFamily = done ? (FontFamily)FindResource("Icons") : SystemFonts.MessageFontFamily,
                    FontSize = 12, FontWeight = FontWeights.SemiBold,
                    Foreground = i <= _step ? Brushes.White : (Brush)FindResource("SubtleBorder"),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            var label = new TextBlock { Text = Titles[i], VerticalAlignment = VerticalAlignment.Center, Opacity = i == _step ? 1 : 0.6 };
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 10, 0) };
            sp.Children.Add(dot);
            sp.Children.Add(label);
            Steps.Children.Add(sp);
        }
        UpdateFooter();
    }

    void UpdateFooter()
    {
        BackButton.Visibility = _step > 0 ? Visibility.Visible : Visibility.Hidden;
        var last = _step == Titles.Length - 1;
        NextButton.Content = last ? "Start Editing" : "Continue";
        var needsSignIn = _step == 1 && !AuthManager.Shared.IsReady;
        NextButton.IsEnabled = !needsSignIn;
        Hint.Text = needsSignIn ? "Sign in to continue" : "";
        SkipButton.Visibility = _step == 2 && !SetupManager.Shared.AllRequiredDone ? Visibility.Visible : Visibility.Collapsed;
    }

    void Back_Click(object sender, RoutedEventArgs e) { if (_step > 0) Step = _step - 1; }
    void Skip_Click(object sender, RoutedEventArgs e) => Step = _step + 1;
    void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_step == Titles.Length - 1) Finished?.Invoke();
        else Step = _step + 1;
    }
}
