using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClaudeVideoEditor.Services;

namespace ClaudeVideoEditor.Views;

public partial class PromptCreatorWindow : Window
{
    static PromptCreatorWindow? _open;
    readonly Dictation _dictation = new();

    public static PromptCreatorWindow Open()
    {
        if (_open != null) { _open.Activate(); return _open; }
        _open = new PromptCreatorWindow { Owner = Application.Current.MainWindow };
        _open.Closed += (_, _) => _open = null;
        _open.Show();
        return _open;
    }

    public PromptCreatorWindow()
    {
        InitializeComponent();
        Icon = Application.Current.MainWindow?.Icon;
        ProjectPicker.ItemsSource = Prefs.Current.Projects;
        ProjectPicker.SelectedItem = Prefs.Current.Projects.FirstOrDefault(p => p.Path == Prefs.Current.SelectedPath)
                                     ?? Prefs.Current.Projects.FirstOrDefault();
        _dictation.PhraseRecognized += phrase =>
        {
            var sep = Notes.Text.Length == 0 || Notes.Text.EndsWith(' ') || Notes.Text.EndsWith('\n') ? "" : " ";
            Notes.AppendText(sep + phrase);
            Notes.CaretIndex = Notes.Text.Length;
            Notes.ScrollToEnd();
        };
        _dictation.PropertyChanged += (_, _) => Dispatcher.BeginInvoke(UpdateMic);
        InputBindings.Add(new KeyBinding(new RelayCommand(async () => await _dictation.ToggleAsync()), Key.D, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => Create_Click(this, new RoutedEventArgs())), Key.Enter, ModifierKeys.Control));
        Closed += async (_, _) => { if (_dictation.IsListening) await _dictation.StopAsync(); };
        UpdateMic();
        UpdateButtons();
    }

    ProjectInfo? Project => ProjectPicker.SelectedItem as ProjectInfo;

    void UpdateMic()
    {
        MicGlyph.Text = _dictation.IsListening ? "" : "";
        MicButton.Background = _dictation.IsListening ? (System.Windows.Media.Brush)FindResource("Bad") : (System.Windows.Media.Brush)FindResource("Accent");
        MicButton.ToolTip = _dictation.IsListening ? "Stop listening (Ctrl+D)" : "Start talking (Ctrl+D)";
        PartialText.Text = _dictation.IsListening ? (_dictation.Partial.Length > 0 ? _dictation.Partial : "Listening…") : "";
        ProblemText.Text = _dictation.Problem;
        SpeechSettingsLink.Visibility = _dictation.HasProblem ? Visibility.Visible : Visibility.Collapsed;
    }

    void UpdateButtons()
    {
        NotesPlaceholder.Visibility = Notes.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        CreateButton.IsEnabled = Notes.Text.Trim().Length > 0 && Project != null && BuildingBar.Visibility != Visibility.Visible;
        CreateButton.Content = PromptBox.Text.Length == 0 ? "Create Prompt" : "Recreate Prompt";
        var hasPrompt = PromptBox.Text.Trim().Length > 0 && Project != null;
        CopyButton.IsEnabled = PutButton.IsEnabled = SendButton.IsEnabled = hasPrompt;
    }

    async void Mic_Click(object sender, RoutedEventArgs e) => await _dictation.ToggleAsync();
    void Notes_TextChanged(object sender, TextChangedEventArgs e) => UpdateButtons();
    void PromptBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateButtons();
    void ProjectPicker_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();
    void Clear_Click(object sender, RoutedEventArgs e) { Notes.Clear(); PromptBox.Clear(); ErrorText.Text = ""; }
    void SpeechSettings_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("ms-settings:privacy-speech") { UseShellExecute = true });

    async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (Project is not ProjectInfo p || Notes.Text.Trim().Length == 0 || BuildingBar.Visibility == Visibility.Visible) return;
        if (_dictation.IsListening) await _dictation.StopAsync();
        BuildingBar.Visibility = BuildingText.Visibility = Visibility.Visible;
        ErrorText.Text = "";
        UpdateButtons();
        try
        {
            var clips = await Task.Run(() => ClipScanner.Scan(p.Path));
            PromptBox.Text = await PromptBuilder.BuildAsync(Notes.Text, p, clips);
        }
        catch (Exception ex) { ErrorText.Text = ex.Message; }
        finally
        {
            BuildingBar.Visibility = BuildingText.Visibility = Visibility.Collapsed;
            UpdateButtons();
        }
    }

    void Copy_Click(object sender, RoutedEventArgs e) => Clipboard.SetText(PromptBox.Text);
    void Put_Click(object sender, RoutedEventArgs e) => Deliver(false);
    void Send_Click(object sender, RoutedEventArgs e) => Deliver(true);

    void Deliver(bool sendNow)
    {
        if (Project is not ProjectInfo p) return;
        if (Application.Current.MainWindow is MainWindow mw)
        {
            var editor = mw.Host.Content as EditorView ?? mw.ShowEditor();
            editor.Deliver(p, PromptBox.Text, sendNow);
            mw.Activate();
        }
        Close();
    }
}
