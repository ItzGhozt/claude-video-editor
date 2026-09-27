using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using ClaudeVideoEditor.Services;

namespace ClaudeVideoEditor.Views;

public partial class LibraryWindow : Window
{
    static LibraryWindow? _open;
    static PromptLibrary Lib => PromptLibrary.Shared;
    static StyleMemory Mem => StyleMemory.Shared;
    SavedPrompt? _current;
    bool _loading;

    public static LibraryWindow Open(bool styleTab = false)
    {
        _open ??= new LibraryWindow { Owner = Application.Current.MainWindow };
        _open.Closed += (_, _) => _open = null;
        _open.Tabs.SelectedIndex = styleTab ? 1 : 0;
        _open.Show();
        _open.Activate();
        return _open;
    }

    public LibraryWindow()
    {
        InitializeComponent();
        Icon = Application.Current.MainWindow?.Icon;
        Action onLib = () => Dispatcher.BeginInvoke(RefreshFolders);
        Action onMem = () => Dispatcher.BeginInvoke(RefreshStyle);
        Lib.Changed += onLib;
        Mem.Changed += onMem;
        Closed += (_, _) => { Lib.Changed -= onLib; Mem.Changed -= onMem; };
        RefreshFolders();
        FolderList.SelectedItem = PromptLibrary.DefaultFolder;
        Mem.Reload();
        RefreshStyle();
    }

    // MARK: Saved prompts

    string Folder => FolderList.SelectedItem as string ?? PromptLibrary.DefaultFolder;

    void RefreshFolders()
    {
        var sel = Folder;
        FolderList.ItemsSource = Lib.Folders.Select(f => f).ToList();
        FolderList.SelectedItem = Lib.Folders.Contains(sel) ? sel : PromptLibrary.DefaultFolder;
        FolderPicker.ItemsSource = Lib.Folders;
        RefreshPrompts();
    }

    void RefreshPrompts()
    {
        var q = SearchBox.Text.Trim();
        var list = q.Length == 0 ? Lib.In(Folder)
            : Lib.Prompts.Where(p => p.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || p.Text.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        var keep = _current;
        PromptList.ItemsSource = list;
        EmptyPrompts.Text = q.Length == 0 ? "No saved prompts here yet.\nClick ☆ on any message you've sent to save it." : "No matches";
        EmptyPrompts.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DeleteFolderButton.IsEnabled = Folder != PromptLibrary.DefaultFolder;
        PromptList.SelectedItem = keep != null && list.Contains(keep) ? keep : null;
        ShowDetail(PromptList.SelectedItem as SavedPrompt);
    }

    void ShowDetail(SavedPrompt? p)
    {
        _current = p;
        DetailPanel.Visibility = p == null ? Visibility.Collapsed : Visibility.Visible;
        NoSelection.Visibility = p == null ? Visibility.Visible : Visibility.Collapsed;
        if (p == null) return;
        _loading = true;
        TitleBox.Text = p.Title;
        TextBox_.Text = p.Text;
        FolderPicker.SelectedItem = p.Folder;
        _loading = false;
    }

    void FolderList_SelectionChanged(object sender, SelectionChangedEventArgs e) { _current = null; RefreshPrompts(); }
    void Search_TextChanged(object sender, TextChangedEventArgs e) => RefreshPrompts();
    void PromptList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowDetail(PromptList.SelectedItem as SavedPrompt);

    void Detail_LostFocus(object sender, RoutedEventArgs e) => SaveDetail();
    void FolderPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _current == null || FolderPicker.SelectedItem is not string f || f == _current.Folder) return;
        SaveDetail();
        _current.Folder = f;
        Lib.Update(_current);
    }

    void SaveDetail()
    {
        if (_loading || _current == null || TextBox_.Text.Trim().Length == 0) return;
        var title = PromptLibrary.CleanTitle(TitleBox.Text, TextBox_.Text);
        if (title == _current.Title && TextBox_.Text == _current.Text) return;
        _current.Title = title;
        _current.Text = TextBox_.Text;
        Lib.Update(_current);
    }

    void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var n = NewFolderBox.Text.Trim();
        if (n.Length == 0) return;
        Lib.AddFolder(n);
        NewFolderBox.Clear();
        FolderList.SelectedItem = n;
    }

    void DeleteFolder_Click(object sender, RoutedEventArgs e)
    {
        var f = Folder;
        if (f == PromptLibrary.DefaultFolder) return;
        if (MessageBox.Show(this, $"Delete the folder \"{f}\"? Its prompts move to Favorites.", "Delete folder",
                            MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            Lib.DeleteFolder(f);
    }

    void DeletePrompt_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        if (MessageBox.Show(this, $"Delete \"{_current.Title}\"?", "Delete prompt", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
        {
            var p = _current;
            _current = null;
            Lib.Delete(p);
        }
    }

    void Copy_Click(object sender, RoutedEventArgs e) { if (_current != null) Clipboard.SetText(TextBox_.Text); }

    void Use_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        SaveDetail();
        if (Application.Current.MainWindow is MainWindow mw && mw.Host.Content is EditorView editor && editor.InsertIntoComposer(TextBox_.Text))
        {
            mw.Activate();
            Close();
        }
        else MessageBox.Show(this, "Open a project first, then try again.", "No project", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // MARK: Style

    void RefreshStyle()
    {
        LikesList.ItemsSource = StyleRows(Mem.Likes);
        DislikesList.ItemsSource = StyleRows(Mem.Dislikes);
    }

    List<UIElement> StyleRows(IEnumerable<string> items)
    {
        var rows = new List<UIElement>();
        foreach (var item in items)
        {
            var remove = new Button { Content = "✕", Padding = new Thickness(6, 0, 6, 0), ToolTip = "Remove", Margin = new Thickness(6, 0, 0, 0) };
            remove.Click += (_, _) => Mem.Remove(item);
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            row.Children.Add(new TextBlock { Text = item, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            rows.Add(row);
        }
        if (rows.Count == 0) rows.Add(new TextBlock { Text = "Nothing yet", Opacity = 0.6 });
        return rows;
    }

    void AddStyle_Click(object sender, RoutedEventArgs e)
    {
        var like = (sender as FrameworkElement)?.Tag as string == "like";
        var box = like ? NewLike : NewDislike;
        Mem.Add(box.Text, like);
        box.Clear();
    }

    void OpenStyleFile_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{StyleMemory.FilePath}\"") { UseShellExecute = true });

    void ClearStyle_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Forget all your style preferences?", "Clear all", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)
            Mem.Clear();
    }
}

/// Small dialogs used from the chat.
public static class LibraryDialogs
{
    /// Asks for a name and folder, then saves the prompt.
    public static void SavePrompt(Window owner, string text)
    {
        if (text.Trim().Length == 0) return;
        var title = new TextBox { Margin = new Thickness(0, 4, 0, 10) };
        var folder = new ComboBox { IsEditable = true, ItemsSource = PromptLibrary.Shared.Folders, Text = PromptLibrary.DefaultFolder, Margin = new Thickness(0, 4, 0, 10) };
        var preview = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Opacity = 0.8 };
        var dlg = Dialog(owner, "Save to Saved Prompts", out var content, out var ok);
        content.Children.Add(new TextBlock { Text = "Name (leave empty to use the first words)" });
        content.Children.Add(title);
        content.Children.Add(new TextBlock { Text = "Folder (pick one or type a new name)" });
        content.Children.Add(folder);
        content.Children.Add(preview);
        ok.Content = "Save";
        ok.Click += (_, _) =>
        {
            var f = string.IsNullOrWhiteSpace(folder.Text) ? PromptLibrary.DefaultFolder : folder.Text.Trim();
            PromptLibrary.Shared.AddFolder(f);
            PromptLibrary.Shared.Add(title.Text, text, f);
            dlg.DialogResult = true;
        };
        title.Focus();
        dlg.ShowDialog();
    }

    /// 👍 / 👎 on a reply: asks what the user liked or didn't and remembers it.
    public static bool Feedback(Window owner, bool like)
    {
        var box = new TextBox { AcceptsReturn = false, Margin = new Thickness(0, 6, 0, 0) };
        var dlg = Dialog(owner, like ? "What did you like?" : "What didn't you like?", out var content, out var ok);
        content.Children.Add(new TextBlock
        {
            Text = (like ? "e.g. the warm grade, cutting on the beat" : "e.g. fast zoom transitions, captions too big") +
                   "\nClaude will remember this for future edits in every project.",
            Opacity = 0.7, TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(box);
        ok.Content = "Remember";
        ok.Click += (_, _) =>
        {
            if (box.Text.Trim().Length == 0) return;
            StyleMemory.Shared.Add(box.Text, like);
            dlg.DialogResult = true;
        };
        box.Focus();
        return dlg.ShowDialog() == true;
    }

    static Window Dialog(Window owner, string title, out StackPanel content, out Button ok)
    {
        content = new StackPanel();
        ok = new Button { Style = (Style)Application.Current.FindResource("Primary"), IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        var root = new DockPanel { Margin = new Thickness(18) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(content);
        return new Window
        {
            Title = title, Content = root, Width = 440, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
        };
    }
}
