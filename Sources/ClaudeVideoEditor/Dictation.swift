import AVFoundation
import Speech

/// Live speech-to-text with Apple's Speech framework. Recognition tasks end on
/// their own after a pause or ~1 minute, so while the mic is on we keep
/// committing what was heard and starting a fresh task -- the user can ramble
/// for as long as they like.
final class Dictation: ObservableObject {
    /// Words heard in the current phrase, still being revised by the recognizer.
    @Published var partial = ""
    @Published var isListening = false
    @Published var problem: String?
    /// Called with each finished phrase so the view can append it to editable notes.
    var onPhrase: ((String) -> Void)?

    private let recognizer = SFSpeechRecognizer(locale: Locale(identifier: "en-US"))
    private let engine = AVAudioEngine()
    private var request: SFSpeechAudioBufferRecognitionRequest?
    private var task: SFSpeechRecognitionTask?

    func toggle() { isListening ? stop() : start() }

    func start() {
        problem = nil
        SFSpeechRecognizer.requestAuthorization { status in
            DispatchQueue.main.async {
                guard status == .authorized else {
                    self.problem = "Speech recognition is off. Turn it on in System Settings › Privacy & Security › Speech Recognition."
                    return
                }
                AVCaptureDevice.requestAccess(for: .audio) { ok in
                    DispatchQueue.main.async {
                        guard ok else {
                            self.problem = "Microphone access is off. Turn it on in System Settings › Privacy & Security › Microphone."
                            return
                        }
                        self.beginAudio()
                    }
                }
            }
        }
    }

    func stop() {
        isListening = false
        engine.stop()
        engine.inputNode.removeTap(onBus: 0)
        request?.endAudio()
        task?.finish()
        commitPartial()
        request = nil
        task = nil
    }

    private func beginAudio() {
        guard let recognizer, recognizer.isAvailable else {
            problem = "Speech recognition isn't available right now."
            return
        }
        let input = engine.inputNode
        let format = input.outputFormat(forBus: 0)
        input.removeTap(onBus: 0)
        input.installTap(onBus: 0, bufferSize: 1024, format: format) { [weak self] buf, _ in
            self?.request?.append(buf)
        }
        engine.prepare()
        do { try engine.start() } catch {
            problem = "Couldn't start the microphone: \(error.localizedDescription)"
            input.removeTap(onBus: 0)
            return
        }
        isListening = true
        newTask()
    }

    private func newTask() {
        guard let recognizer else { return }
        let req = SFSpeechAudioBufferRecognitionRequest()
        req.shouldReportPartialResults = true
        req.addsPunctuation = true
        if recognizer.supportsOnDeviceRecognition { req.requiresOnDeviceRecognition = true }
        request = req
        task = recognizer.recognitionTask(with: req) { [weak self] result, error in
            DispatchQueue.main.async {
                // Late callbacks from a task we already stopped would re-add its phrase.
                guard let self, self.request === req else { return }
                if let result {
                    self.partial = result.bestTranscription.formattedString
                }
                if error != nil || result?.isFinal == true {
                    self.commitPartial()
                    // Still listening: keep going with a fresh task.
                    if self.isListening { self.newTask() }
                }
            }
        }
    }

    private func commitPartial() {
        let p = partial.trimmingCharacters(in: .whitespaces)
        guard !p.isEmpty else { return }
        partial = ""
        onPhrase?(p)
    }
}
