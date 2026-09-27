using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using ClaudeVideoEditor.Services;

namespace ClaudeVideoEditor.Views;

public partial class SetupPanel : UserControl
{
    public SetupPanel()
    {
        InitializeComponent();
        List.ItemsSource = SetupManager.Shared.Items;
        Loaded += async (_, _) => await SetupManager.Shared.RefreshAsync();
    }

    async void Refresh_Click(object sender, RoutedEventArgs e) => await SetupManager.Shared.RefreshAsync();

    async void Install_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SetupItem item) await SetupManager.Shared.InstallAsync(item);
    }

    /// The key-entry row only shows for the ElevenLabs item, until a key is saved.
    void KeyRow_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement row || row.DataContext is not SetupItem item) return;
        void Apply() => row.Visibility = item.IsKeyEntry && !item.IsOk ? Visibility.Visible : Visibility.Collapsed;
        Apply();
        item.PropertyChanged += (_, _) => row.Dispatcher.BeginInvoke(Apply);
    }

    async void SaveKey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement b || b.Parent is not DockPanel row) return;
        var box = row.Children.OfType<PasswordBox>().FirstOrDefault();
        if (box == null || box.Password.Length == 0) return;
        await SetupManager.Shared.SaveElevenLabsKeyAsync(box.Password);
        box.Clear();
    }

    void Link_Navigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
