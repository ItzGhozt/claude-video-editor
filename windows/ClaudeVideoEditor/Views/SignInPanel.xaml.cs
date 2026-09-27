using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using ClaudeVideoEditor.Services;

namespace ClaudeVideoEditor.Views;

public partial class SignInPanel : UserControl
{
    static AuthManager Auth => AuthManager.Shared;

    public SignInPanel()
    {
        InitializeComponent();
        PropertyChangedEventHandler onChange = (_, _) => Dispatcher.BeginInvoke(Update);
        Loaded += async (_, _) => { Auth.PropertyChanged += onChange; Update(); await Auth.RefreshAsync(); };
        Unloaded += (_, _) => Auth.PropertyChanged -= onChange;
        Update();
    }

    void Update()
    {
        var s = Auth.State;
        CheckingPanel.Visibility = Vis(s == AuthState.Checking);
        NoClaudePanel.Visibility = Vis(s == AuthState.NoClaude);
        SignedOutPanel.Visibility = Vis(s == AuthState.SignedOut);
        SigningInPanel.Visibility = Vis(s == AuthState.SigningIn);
        SignedInPanel.Visibility = Vis(s is AuthState.SignedIn or AuthState.ApiKey);
        AccountTitle.Text = Auth.Title;
        AccountDetail.Text = Auth.Detail;
        AccountIcon.Text = s == AuthState.ApiKey ? "" : "";
        LoginLinkBlock.Visibility = Vis(Auth.LoginUrl.Length > 0);
        if (Uri.TryCreate(Auth.LoginUrl, UriKind.Absolute, out var u)) LoginLink.NavigateUri = u;
        ErrorText.Text = Auth.Error;
        ErrorText.Visibility = Vis(Auth.Error.Length > 0);
    }

    static Visibility Vis(bool b) => b ? Visibility.Visible : Visibility.Collapsed;

    async void InstallClaude_Click(object sender, RoutedEventArgs e)
    {
        var item = SetupManager.Shared.Claude;
        InstallClaudeButton.IsEnabled = false;
        InstallProgress.Visibility = Visibility.Visible;
        InstallMessage.Text = "";
        await SetupManager.Shared.InstallAsync(item);
        InstallProgress.Visibility = Visibility.Collapsed;
        InstallClaudeButton.IsEnabled = true;
        if (item.IsFailed) InstallMessage.Text = item.Message;
        await Auth.RefreshAsync();
    }

    void SignIn_Click(object sender, RoutedEventArgs e) => Auth.SignInWithClaude();
    void Cancel_Click(object sender, RoutedEventArgs e) => Auth.CancelSignIn();
    void SubmitCode_Click(object sender, RoutedEventArgs e) { Auth.SubmitCode(CodeBox.Text); CodeBox.Clear(); }

    async void UseKey_Click(object sender, RoutedEventArgs e)
    {
        UseKeyButton.IsEnabled = false;
        if (await Auth.UseApiKeyAsync(ApiKeyBox.Password)) ApiKeyBox.Clear();
        UseKeyButton.IsEnabled = true;
    }

    async void SignOut_Click(object sender, RoutedEventArgs e)
    {
        var note = Auth.State == AuthState.ApiKey
            ? "This removes the API key from Windows Credential Manager."
            : "This also signs Claude Code out in the terminal, because they share one login.";
        if (MessageBox.Show(Window.GetWindow(this), note, "Sign out?", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            await Auth.SignOutAsync();
    }

    void Link_Navigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
