using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using Windows.Media.SpeechRecognition;

namespace ClaudeVideoEditor.Services;

/// Live speech-to-text with Windows' built-in speech recognition (continuous
/// dictation). Finished phrases go to `PhraseRecognized`; the words still being
/// recognised are in `Partial`.
public class Dictation : INotifyPropertyChanged
{
    SpeechRecognizer? _rec;
    bool _listening;
    string _partial = "", _problem = "";
    public bool IsListening { get => _listening; private set { _listening = value; Notify(); } }
    public string Partial { get => _partial; private set { _partial = value; Notify(); } }
    public string Problem { get => _problem; private set { _problem = value; Notify(); Notify(nameof(HasProblem)); } }
    public bool HasProblem => Problem.Length > 0;
    public event Action<string>? PhraseRecognized;

    // HRESULT when "Online speech recognition" is off in Settings > Privacy > Speech.
    const int SpeechPrivacyDeclined = unchecked((int)0x80045509);

    public async Task ToggleAsync()
    {
        if (IsListening) await StopAsync(); else await StartAsync();
    }

    public async Task StartAsync()
    {
        Problem = "";
        try
        {
            _rec = new SpeechRecognizer();
            _rec.Constraints.Add(new SpeechRecognitionTopicConstraint(SpeechRecognitionScenario.Dictation, "dictation"));
            var compiled = await _rec.CompileConstraintsAsync();
            if (compiled.Status != SpeechRecognitionResultStatus.Success)
            {
                Problem = "Speech recognition isn't available (" + compiled.Status + ").";
                return;
            }
            _rec.ContinuousRecognitionSession.AutoStopSilenceTimeout = TimeSpan.FromMinutes(10);
            var ui = Application.Current.Dispatcher;
            _rec.HypothesisGenerated += (_, e) => ui.BeginInvoke(() => Partial = e.Hypothesis.Text);
            _rec.ContinuousRecognitionSession.ResultGenerated += (_, e) => ui.BeginInvoke(() =>
            {
                Partial = "";
                var t = e.Result.Text.Trim();
                if (t.Length > 0 && e.Result.Confidence != SpeechRecognitionConfidence.Rejected) PhraseRecognized?.Invoke(t);
            });
            _rec.ContinuousRecognitionSession.Completed += (_, e) => ui.BeginInvoke(() =>
            {
                if (IsListening && e.Status != SpeechRecognitionResultStatus.Success)
                    Problem = "Listening stopped (" + e.Status + ").";
                IsListening = false;
            });
            await _rec.ContinuousRecognitionSession.StartAsync();
            IsListening = true;
        }
        catch (Exception e) when (e.HResult == SpeechPrivacyDeclined)
        {
            Problem = "Turn on \"Online speech recognition\" in Windows Settings › Privacy & security › Speech, then try again.";
        }
        catch (UnauthorizedAccessException)
        {
            Problem = "Microphone access is off. Turn on \"Let desktop apps access your microphone\" in Windows Settings › Privacy & security › Microphone.";
        }
        catch (Exception e)
        {
            Problem = "Couldn't start listening: " + e.Message;
        }
    }

    public async Task StopAsync()
    {
        IsListening = false;
        if (_rec == null) return;
        try { await _rec.ContinuousRecognitionSession.StopAsync(); } catch { }
        if (Partial.Trim() is { Length: > 0 } p) PhraseRecognized?.Invoke(p);
        Partial = "";
        _rec.Dispose();
        _rec = null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Notify([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
