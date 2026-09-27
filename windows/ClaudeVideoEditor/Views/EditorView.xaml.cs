using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ClaudeVideoEditor.Services;
using Microsoft.Win32;

namespace ClaudeVideoEditor.Views;

public class ChatTemplateSelector : DataTemplateSelector
{
    public DataTemplate? User { get; set; }
    public DataTemplate? Assistant { get; set; }
    public DataTemplate? Tool { get; set; }
    public DataTemplate? Notice { get; set; }
    public DataTemplate? Error { get; set; }
    public DataTemplate? Approval { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) => (item as ChatItem)?.Kind switch
    {
        ChatKind.User => User,
        ChatKind.Assistant => Assistant,
        ChatKind.Tool or ChatKind.ToolResult => Tool,
        ChatKind.Notice => Notice,
        ChatKind.Error => Error,
        ChatKind.Approval => Approval,
        _ => null,
    };
}

public record QuickAction(string Title, string Prompt)
{
    public static readonly QuickAction[] All =
    {
        new("✨ Highlight reel", "Make a highlight reel from the best moments in this folder. Show me the list of moments you picked before you render."),
        new("✂ Cut the dead air", "Cut out the filler words, false starts and dead space, keeping everything in order. Show me the cut list before rendering."),
        new("▯ Export 9:16", "Export this as a 9:16 reel: 1080x1920 h264, 30 fps, AAC 48 kHz stereo. Use a blurred-edge fill if the source isn't already 9:16."),
        new("💬 Subtitles", "Transcribe the speech and burn in clean, readable subtitles."),
        new("🎨 Color grade", "Suggest a color grade for this footage, render a before/after still for me to check, then apply it once I approve."),
        new("📋 What's here?", "Give me a quick overview of the footage in this folder: how many clips, total length, what's in them, and any finished exports."),
    };
}

public partial class EditorView : UserControl
{
    /// One session per project, kept for the life of the app.
    static readonly Dictionary<string, ClaudeSession> Sessions = new(StringComparer.OrdinalIgnoreCase);
    public static ClaudeSession SessionFor(ProjectInfo p)
    {
        if (!Sessions.TryGetValue(p.Path, out var s)) Sessions[p.Path] = s = new ClaudeSession(p);
        return s;
    }

    ClaudeSession? _session;
    List<Clip> _clips = new();
    readonly DispatcherTimer _playerTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    bool _playing, _seeking;
    Point _dragStart;

    public EditorView()
    {
        InitializeComponent();
        foreach (var a in QuickAction.All)
        {
            var b = new Button { Content = a.Title, Style = (Style)FindResource("Chip"), ToolTip = a.Prompt };
            b.Click += (_, _) =>
            {
                Composer.Text = Composer.Text.Length == 0 ? a.Prompt : Composer.Text + "\n\n" + a.Prompt;
                Composer.Focus();
                Composer.CaretIndex = Composer.Text.Length;
            };
            QuickActions.Children.Add(b);
        }
        var saved = new Button { Content = "☆ Saved ▾", Style = (Style)FindResource("Chip"), ToolTip = "Insert a saved prompt, or save this message" };
        saved.Click += (_, _) => ShowSavedMenu(saved);
        QuickActions.Children.Add(saved);
        _playerTimer.Tick += (_, _) => UpdatePlayerTime();
        SetupManager.Shared.Changed += UpdateSetupButton;
        Loaded += async (_, _) =>
        {
            ReloadProjects();
            await SetupManager.Shared.RefreshAsync();
            UpdateSetupButton();
            await CheckSignInAsync();
        };
        Unloaded += (_, _) => SetupManager.Shared.Changed -= UpdateSetupButton;
        InputBindings.Add(new KeyBinding(new RelayCommand(() => PromptCreatorWindow.Open()), Key.P, ModifierKeys.Control | ModifierKeys.Shift));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => LibraryWindow.Open()), Key.L, ModifierKeys.Control | ModifierKeys.Shift));
    }

    static async Task CheckSignInAsync()
    {
        await AuthManager.Shared.RefreshAsync();
        if (AuthManager.Shared.IsReady || DevTools.SnapshotMode) return;
        var dlg = new Window
        {
            Title = "Sign in", Width = 620, Height = 600, Content = new SignInPanel(),
            Owner = Application.Current.MainWindow, WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        PropertyChangedEventHandler h = (_, _) => dlg.Dispatcher.BeginInvoke(() => { if (AuthManager.Shared.IsReady && dlg.IsVisible) dlg.Close(); });
        AuthManager.Shared.PropertyChanged += h;
        dlg.ShowDialog();
        AuthManager.Shared.PropertyChanged -= h;
    }

    void UpdateSetupButton() => Dispatcher.BeginInvoke(() =>
        FinishSetupButton.Visibility = SetupManager.Shared.AllRequiredDone ? Visibility.Collapsed : Visibility.Visible);

    // MARK: Projects

    void ReloadProjects()
    {
        var prefs = Prefs.Current;
        ProjectList.ItemsSource = null;
        ProjectList.ItemsSource = prefs.Projects;
        NoProjectsHint.Visibility = prefs.Projects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var sel = prefs.Projects.FirstOrDefault(p => p.Path == prefs.SelectedPath) ?? prefs.Projects.FirstOrDefault();
        ProjectList.SelectedItem = sel;
        if (sel == null) Select(null);
    }

    public void AddProject(string dir)
    {
        dir = Path.GetFullPath(dir).TrimEnd('\\');
        var prefs = Prefs.Current;
        if (!prefs.Projects.Any(p => p.Path.Equals(dir, StringComparison.OrdinalIgnoreCase)))
            prefs.Projects.Add(new ProjectInfo { Name = Path.GetFileName(dir), Path = dir });
        prefs.SelectedPath = dir;
        prefs.Save();
        ReloadProjects();
    }

    void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Choose a folder of footage. Claude will work inside it.",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) AddProject(dlg.FolderName);
    }

    void ProjectList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            foreach (var p in paths.Where(Directory.Exists)) AddProject(p);
    }

    void RemoveProject_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectList.SelectedItem is not ProjectInfo p) return;
        Prefs.Current.Projects.Remove(p);
        Prefs.Current.Save();
        ReloadProjects();
    }

    void ProjectList_SelectionChanged(object sender, SelectionChangedEventArgs e) => Select(ProjectList.SelectedItem as ProjectInfo);

    void Select(ProjectInfo? p)
    {
        if (_session != null)
        {
            _session.Draft = Composer.Text;
            _session.PropertyChanged -= Session_PropertyChanged;
            _session.Items.CollectionChanged -= Items_CollectionChanged;
        }
        StopPlayer();
        var mounted = p != null && Directory.Exists(p.Path);
        NoProjectPanel.Visibility = mounted ? Visibility.Collapsed : Visibility.Visible;
        ChatPanel.Visibility = ClipsPanel.Visibility = mounted ? Visibility.Visible : Visibility.Collapsed;
        if (p == null || !mounted) { _session = null; return; }

        Prefs.Current.SelectedPath = p.Path;
        Prefs.Current.Save();
        _session = SessionFor(p);
        _session.PropertyChanged += Session_PropertyChanged;
        _session.Items.CollectionChanged += Items_CollectionChanged;
        ChatItems.ItemsSource = _session.Items;
        Composer.Text = _session.Draft;
        ProjectTitle.Text = EmptyTitle.Text = p.Name;
        ProjectSubtitle.Text = p.Path;
        UpdateChatState();
        RescanClips();
        ScrollToEnd();
    }

    void OpenProjectFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_session?.Project.Path is string dir && Directory.Exists(dir))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    // MARK: Chat

    void Session_PropertyChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (e.PropertyName == nameof(ClaudeSession.TurnsFinished)) RescanClips();
        if (e.PropertyName == nameof(ClaudeSession.Draft) && _session != null && Composer.Text != _session.Draft) Composer.Text = _session.Draft;
        UpdateChatState();
        if (e.PropertyName == nameof(ClaudeSession.LiveText)) ScrollToEnd();
    });

    void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Dispatcher.BeginInvoke(() => { UpdateChatState(); ScrollToEnd(); });

    void UpdateChatState()
    {
        if (_session == null) return;
        EmptyChat.Visibility = _session.Items.Count == 0 && !_session.HasLiveText ? Visibility.Visible : Visibility.Collapsed;
        LiveBubble.Visibility = _session.HasLiveText ? Visibility.Visible : Visibility.Collapsed;
        LiveText.Text = _session.LiveText;
        var waitingOnUser = _session.Items.Any(i => i.IsPending);
        WorkingRow.Visibility = _session.IsBusy && !waitingOnUser ? Visibility.Visible : Visibility.Collapsed;
        SendButton.Visibility = _session.IsBusy ? Visibility.Collapsed : Visibility.Visible;
        StopButton.Visibility = _session.IsBusy ? Visibility.Visible : Visibility.Collapsed;
        SendButton.IsEnabled = Composer.Text.Trim().Length > 0;
    }

    void ScrollToEnd() => Dispatcher.BeginInvoke(ChatScroll.ScrollToEnd, DispatcherPriority.Background);

    void Send_Click(object sender, RoutedEventArgs e) => SendDraft();
    void Stop_Click(object sender, RoutedEventArgs e) => _session?.Stop();
    void Composer_TextChanged(object sender, TextChangedEventArgs e) { if (_session != null) SendButton.IsEnabled = Composer.Text.Trim().Length > 0; }

    void Composer_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            SendDraft();
        }
    }

    void SendDraft()
    {
        if (_session == null || _session.IsBusy || Composer.Text.Trim().Length == 0) return;
        _session.Send(Composer.Text);
        Composer.Clear();
    }

    void NewChat_Click(object sender, RoutedEventArgs e)
    {
        if (_session == null) return;
        if (MessageBox.Show(Window.GetWindow(this), "The current conversation will be cleared. Files in the folder are not touched.",
                            $"Start a new chat for {_session.Project.Name}?", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            _session.NewChat();
    }

    /// Put text in a project's message box (used by the Prompt Creator).
    public void Deliver(ProjectInfo p, string text, bool sendNow)
    {
        var target = Prefs.Current.Projects.FirstOrDefault(x => x.Path.Equals(p.Path, StringComparison.OrdinalIgnoreCase));
        if (target != null) ProjectList.SelectedItem = target;
        var s = SessionFor(p);
        if (sendNow && !s.IsBusy) { s.Send(text); Composer.Clear(); }
        else
        {
            Composer.Text = Composer.Text.Length == 0 ? text : Composer.Text + "\n\n" + text;
            Composer.Focus();
        }
    }

    // MARK: Saved prompts and style feedback

    /// Puts text in the message box of the open project. False if there is none.
    public bool InsertIntoComposer(string text)
    {
        if (_session == null) return false;
        Composer.Text = Composer.Text.Length == 0 ? text : Composer.Text + "\n\n" + text;
        Composer.Focus();
        Composer.CaretIndex = Composer.Text.Length;
        return true;
    }

    void ShowSavedMenu(Button anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor };
        var lib = PromptLibrary.Shared;
        foreach (var folder in lib.Folders)
        {
            var items = lib.In(folder);
            if (items.Count == 0) continue;
            var sub = new MenuItem { Header = folder };
            foreach (var p in items)
            {
                var mi = new MenuItem { Header = p.Title, ToolTip = p.Preview };
                mi.Click += (_, _) => InsertIntoComposer(p.Text);
                sub.Items.Add(mi);
            }
            menu.Items.Add(sub);
        }
        if (lib.Prompts.Count == 0) menu.Items.Add(new MenuItem { Header = "No saved prompts yet", IsEnabled = false });
        menu.Items.Add(new Separator());
        var save = new MenuItem { Header = "Save Current Message…", IsEnabled = Composer.Text.Trim().Length > 0 };
        save.Click += (_, _) => LibraryDialogs.SavePrompt(Window.GetWindow(this), Composer.Text);
        menu.Items.Add(save);
        var open = new MenuItem { Header = "Open Library…" };
        open.Click += (_, _) => LibraryWindow.Open();
        menu.Items.Add(open);
        menu.IsOpen = true;
    }

    void SavePrompt_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not ChatItem i) return;
        LibraryDialogs.SavePrompt(Window.GetWindow(this), i.Text);
        SaveButton_Loaded(sender, e);
    }

    void SaveButton_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && ItemOf(sender) is ChatItem i)
            b.Content = PromptLibrary.Shared.Contains(i.Text) ? "★ Saved" : "☆ Save prompt";
    }

    void Like_Click(object sender, RoutedEventArgs e) => Feedback(sender, true);
    void Dislike_Click(object sender, RoutedEventArgs e) => Feedback(sender, false);
    void Feedback(object sender, bool like)
    {
        if (LibraryDialogs.Feedback(Window.GetWindow(this), like) && sender is Button b) { b.Opacity = 1; b.FontWeight = FontWeights.Bold; }
    }

    // MARK: Approvals

    ChatItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as ChatItem;
    void Allow_Click(object sender, RoutedEventArgs e) { if (ItemOf(sender) is ChatItem i) _session?.Respond(i, ApprovalState.Allowed); UpdateChatState(); }
    void AlwaysAllow_Click(object sender, RoutedEventArgs e) { if (ItemOf(sender) is ChatItem i) _session?.Respond(i, ApprovalState.AlwaysAllowed); UpdateChatState(); }
    void Deny_Click(object sender, RoutedEventArgs e) { if (ItemOf(sender) is ChatItem i) _session?.Respond(i, ApprovalState.Denied); UpdateChatState(); }
    void AlwaysButton_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && ItemOf(sender) is ChatItem i) b.Visibility = i.SuggestedRule == null ? Visibility.Collapsed : Visibility.Visible;
    }

    // MARK: Clips

    void RescanClips()
    {
        if (_session == null) return;
        var root = _session.Project.Path;
        var selected = (ClipList.SelectedItem as Clip)?.FullPath;
        Task.Run(() => ClipScanner.Scan(root)).ContinueWith(t => Dispatcher.BeginInvoke(() =>
        {
            if (_session?.Project.Path != root) return;
            _clips = t.Result;
            ApplyClipFilter();
            if (selected != null) ClipList.SelectedItem = _clips.FirstOrDefault(c => c.FullPath == selected);
        }));
    }

    void ApplyClipFilter()
    {
        var f = ClipFilter.Text.Trim();
        ClipList.ItemsSource = f.Length == 0 ? _clips : _clips.Where(c => c.Relative.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        NoClipsHint.Visibility = _clips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void ClipFilter_TextChanged(object sender, TextChangedEventArgs e) => ApplyClipFilter();
    void RefreshClips_Click(object sender, RoutedEventArgs e) => RescanClips();

    void ClipList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        StopPlayer();
        if (ClipList.SelectedItem is not Clip c)
        {
            ClipInfo.Visibility = PlayerControls.Visibility = Visibility.Collapsed;
            PlayerHint.Visibility = Visibility.Visible;
            PlayerHintText.Text = "Select a clip to preview";
            return;
        }
        ClipInfo.Visibility = PlayerControls.Visibility = Visibility.Visible;
        ClipName.Text = c.Name;
        ClipMeta.Text = c.Info;
        PlayerHint.Visibility = Visibility.Collapsed;
        Player.Source = new Uri(c.FullPath);
        Player.Play();
        Player.Pause();   // show the first frame
    }

    void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        Seek.Maximum = Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan.TotalSeconds : 0;
        UpdatePlayerTime();
    }

    void Player_MediaEnded(object sender, RoutedEventArgs e) { SetPlaying(false); Player.Position = TimeSpan.Zero; }

    void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        PlayerHint.Visibility = Visibility.Visible;
        PlayerHintText.Text = "Windows can't preview this file. HEVC/H.265 clips need the free \"HEVC Video Extensions\" from the Microsoft Store.";
    }

    void Play_Click(object sender, RoutedEventArgs e) => SetPlaying(!_playing);

    void SetPlaying(bool play)
    {
        _playing = play;
        if (play) { Player.Play(); _playerTimer.Start(); } else { Player.Pause(); _playerTimer.Stop(); }
        PlayGlyph.Text = play ? "" : "";
    }

    void StopPlayer()
    {
        SetPlaying(false);
        Player.Stop();
        Player.Source = null;
    }

    void UpdatePlayerTime()
    {
        if (!_seeking) Seek.Value = Player.Position.TotalSeconds;
        var total = Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan : TimeSpan.Zero;
        TimeText.Text = $"{Player.Position:m\\:ss} / {total:m\\:ss}";
    }

    void Seek_MouseDown(object sender, MouseButtonEventArgs e) => _seeking = true;
    void Seek_MouseUp(object sender, MouseButtonEventArgs e)
    {
        Player.Position = TimeSpan.FromSeconds(Seek.Value);
        _seeking = false;
        UpdatePlayerTime();
    }

    void UseInChat_Click(object sender, RoutedEventArgs e)
    {
        if (ClipList.SelectedItem is not Clip c) return;
        var token = $"`{c.Relative}`";
        Composer.Text = Composer.Text.Length == 0 ? token + " " : Composer.Text.TrimEnd() + " " + token + " ";
        Composer.Focus();
        Composer.CaretIndex = Composer.Text.Length;
    }

    void ShowInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (ClipList.SelectedItem is Clip c)
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{c.FullPath}\"") { UseShellExecute = true });
    }

    // Drag a clip into the message box to insert its name.
    void ClipList_MouseDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(null);
    void ClipList_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || ClipList.SelectedItem is not Clip c) return;
        var d = e.GetPosition(null) - _dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        DragDrop.DoDragDrop(ClipList, $"`{c.Relative}`", DragDropEffects.Copy);
    }

    // MARK: Menus

    void PromptCreator_Click(object sender, RoutedEventArgs e) => PromptCreatorWindow.Open();
    void Library_Click(object sender, RoutedEventArgs e) => LibraryWindow.Open();
    void Settings_Click(object sender, RoutedEventArgs e) => SimpleWindows.Settings();
    void Guide_Click(object sender, RoutedEventArgs e) => SimpleWindows.Guide();
    void Credits_Click(object sender, RoutedEventArgs e) => SimpleWindows.Credits();
    void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
    void Url_Click(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.Tag is string url) SimpleWindows.OpenUrl(url); }
}

public class RelayCommand(Action run) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => run();
}
