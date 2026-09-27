using System.Windows;
using ClaudeVideoEditor.Services;
using ClaudeVideoEditor.Views;

namespace ClaudeVideoEditor;

/// First run shows onboarding; afterwards the editor, with a sign-in dialog if the
/// account was signed out in the meantime.
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ShowCurrent();
        Activated += async (_, _) => await AuthManager.Shared.RefreshAsync();
    }

    public void ShowCurrent()
    {
        if (Prefs.Current.OnboardingDone) ShowEditor();
        else ShowOnboarding();
    }

    public OnboardingView ShowOnboarding(int step = 0)
    {
        var v = new OnboardingView(step);
        v.Finished += () => { Prefs.Current.OnboardingDone = true; Prefs.Current.Save(); ShowEditor(); };
        Host.Content = v;
        return v;
    }

    public EditorView ShowEditor()
    {
        var v = new EditorView();
        Host.Content = v;
        return v;
    }
}
