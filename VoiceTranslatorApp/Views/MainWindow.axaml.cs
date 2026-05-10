using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VoiceTranslatorApp.Services;

namespace VoiceTranslatorApp.Views
{
    public partial class MainWindow : Window
    {
        private enum ListenTranslatePhase
        {
            Listening,
            Busy,
        }

        private const string StatusIdle =
            "Перевод не запущен. Выберите языки и нажмите «Начать перевод».";

        private const string StatusStep1Recognizing =
            "Шаг 1. Распознавание: говорите в микрофон — речь отображается в поле «Оригинальный текст».";

        private const string StatusStep2Silence =
            "Шаг 2. Нет нового текста 2 с — считывание голоса остановлено.";

        private const string StatusStep3Translate =
            "Шаг 3. Перевод: текст переводится на выбранный язык (озвучка отключена).";

        private const string StatusStep4Speak =
            "Шаг 4. Озвучка: воспроизводится перевод (распознавание и перевод отключены).";

        private const string StatusStep5And6NewCycle =
            "Шаг 5–6. Тексты удалены, озвучка отключена. Снова шаг 1: слушаю речь — говорите в микрофон.";

        private bool _isTranslationRunning;
        private ListenTranslatePhase _phase = ListenTranslatePhase.Listening;

        private const string VoskRuModelDir = "vosk-model-small-ru-0.22";
        private const string VoskEnModelDir = "vosk-model-small-en-us-0.15";
        private readonly IVoiceCaptureService _voiceCaptureService = new VoiceCaptureService();
        private IRealtimeSpeechToTextService? _speechToTextService;
        private readonly Dictionary<string, string> _inputDevices = [];
        private readonly StringBuilder _recognizedFinalText = new();
        private readonly object _audioRecordingSync = new();
        private string _recognizedPartialText = string.Empty;
        private WaveFileWriter? _sessionWaveWriter;
        private string? _sessionRecordingPath;
        private long _sessionRecordedBytes;
        private readonly ITranslationService _translationService = new YandexTranslateService();
        private readonly ITextToSpeechService _ttsService = new YandexTextToSpeechService();
        private readonly AudioPlaybackService _audioPlaybackService = new AudioPlaybackService();

        private string _sessionSourceLangCode = "ru";
        private string _sessionTargetLangCode = "en";
        private string? _sessionInputDeviceId;

        private CancellationTokenSource? _silenceCts;
        private bool _utteranceStarted;

        /// <summary>Vosk шлёт PartialTextUpdated почти на каждый чанк — таймер тишины сбрасываем только при смене текста.</summary>
        private string _lastCombinedTextForSilence = string.Empty;

        private static readonly Dictionary<string, string> LanguageDisplayToCode =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Русский"] = "ru",
                ["Английский"] = "en",
                ["Немецкий"] = "de",
                ["Французский"] = "fr",
                ["Испанский"] = "es",
                ["Португальский"] = "pt",
            };

        /// <summary>
        /// Порядок совпадает с элементами ComboBox в MainWindow.axaml (оба списка языков одинаковые).
        /// </summary>
        private static readonly string[] LanguageCodesByComboOrder = ["ru", "en", "de", "fr", "es", "pt"];

        public MainWindow()
        {
            InitializeComponent();
            _voiceCaptureService.AudioChunkCaptured += OnAudioChunkCaptured;
            LoadAudioDevices();
            SetTranslationPhaseStatus(StatusIdle);
        }

        private void SetTranslationPhaseStatus(string message)
        {
            void Apply()
            {
                TranslationPhaseStatusTextBlock.Text = message;
            }

            if (Dispatcher.UIThread.CheckAccess())
            {
                Apply();
            }
            else
            {
                Dispatcher.UIThread.Post(Apply);
            }
        }

        private void LoadAudioDevices()
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();

                var inputDevicePairs = enumerator
                    .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                    .Select(device => device.FriendlyName)
                    .GroupBy(device => device)
                    .Select(group => group.First())
                    .ToList();

                var outputDevices = enumerator
                    .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                    .Select(device => device.FriendlyName)
                    .Distinct()
                    .ToList();

                _inputDevices.Clear();
                foreach (var deviceName in inputDevicePairs)
                {
                    _inputDevices[deviceName] = deviceName;
                }

                SetComboBoxItems(InputDeviceComboBox, inputDevicePairs, "Устройства ввода не найдены");
                SetComboBoxItems(OutputDeviceComboBox, outputDevices, "Устройства вывода не найдены");
            }
            catch
            {
                SetComboBoxItems(InputDeviceComboBox, [], "Не удалось получить устройства ввода");
                SetComboBoxItems(OutputDeviceComboBox, [], "Не удалось получить устройства вывода");
            }
        }

        private static void SetComboBoxItems(ComboBox comboBox, IReadOnlyList<string> devices, string fallback)
        {
            if (devices.Count == 0)
            {
                comboBox.ItemsSource = new[] { fallback };
                comboBox.SelectedIndex = 0;
                comboBox.IsEnabled = false;
                return;
            }

            comboBox.ItemsSource = devices;
            comboBox.SelectedIndex = 0;
            comboBox.IsEnabled = true;
        }

        private void ShowVoiceModels(object? sender, RoutedEventArgs e)
        {
            VoiceModelsPanel.IsVisible = true;
            TranslationPanel.IsVisible = false;

            VoiceModelsButton.Background = new SolidColorBrush(Color.Parse("#2563EB"));
            VoiceModelsButton.Foreground = Brushes.White;
            TranslationButton.Background = new SolidColorBrush(Color.Parse("#1E293B"));
            TranslationButton.Foreground = new SolidColorBrush(Color.Parse("#E2E8F0"));
        }

        private void ShowTranslation(object? sender, RoutedEventArgs e)
        {
            VoiceModelsPanel.IsVisible = false;
            TranslationPanel.IsVisible = true;

            TranslationButton.Background = new SolidColorBrush(Color.Parse("#2563EB"));
            TranslationButton.Foreground = Brushes.White;
            VoiceModelsButton.Background = new SolidColorBrush(Color.Parse("#1E293B"));
            VoiceModelsButton.Foreground = new SolidColorBrush(Color.Parse("#E2E8F0"));
        }

        private void StartTranslation(object? sender, RoutedEventArgs e)
        {
            _isTranslationRunning = !_isTranslationRunning;

            if (_isTranslationRunning)
            {
                var selectedInputName = InputDeviceComboBox.SelectedItem?.ToString();
                var selectedInputId = selectedInputName is not null && _inputDevices.TryGetValue(selectedInputName, out var id)
                    ? id
                    : null;

                CancelSilenceTimer();

                _phase = ListenTranslatePhase.Listening;
                _utteranceStarted = false;
                _lastCombinedTextForSilence = string.Empty;
                _sessionInputDeviceId = selectedInputId;

                _recognizedFinalText.Clear();
                _recognizedPartialText = string.Empty;
                OriginalTextTextBox.Text = string.Empty;
                TranslatedTextTextBox.Text = string.Empty;

                _sessionSourceLangCode = GetSelectedLanguageCode(SourceLanguageComboBox);
                _sessionTargetLangCode = GetSelectedLanguageCode(TargetLanguageComboBox);

                var modelPath = FindVoskModelPathForLanguage(_sessionSourceLangCode);
                if (modelPath is null || !Directory.Exists(modelPath))
                {
                    _isTranslationRunning = false;
                    _sessionInputDeviceId = null;
                    OriginalTextTextBox.Text = BuildModelNotFoundMessageForLanguage(_sessionSourceLangCode);
                    SetTranslationPhaseStatus("Ошибка: модель распознавания не найдена. См. текст в поле слева.");
                    return;
                }

                try
                {
                    ReplaceSpeechToTextService(modelPath);
                }
                catch (Exception ex)
                {
                    _isTranslationRunning = false;
                    _sessionInputDeviceId = null;
                    OriginalTextTextBox.Text = $"Ошибка инициализации STT: {ex.Message}";
                    SetTranslationPhaseStatus($"Ошибка инициализации распознавания: {ex.Message}");
                    return;
                }

                if (_speechToTextService is null)
                {
                    _isTranslationRunning = false;
                    _sessionInputDeviceId = null;
                    SetTranslationPhaseStatus(StatusIdle);
                    return;
                }

                BeginRecordingSession();

                try
                {
                    _speechToTextService.Start();
                }
                catch (Exception ex)
                {
                    _isTranslationRunning = false;
                    _sessionInputDeviceId = null;
                    FinishRecordingSession();
                    ReleaseSpeechToTextService();
                    OriginalTextTextBox.Text = $"Ошибка распознавания: {ex.Message}";
                    SetTranslationPhaseStatus($"Ошибка запуска распознавания: {ex.Message}");
                    return;
                }

                SetTranslationLanguageSelectorsEnabled(isEnabled: false);

                _voiceCaptureService.Start(selectedInputId);
                SetTranslationPhaseStatus(StatusStep1Recognizing);
                StartTranslationButton.Content = "Перевод идёт";
                StartTranslationButton.Background = new SolidColorBrush(Color.Parse("#DC2626"));
                return;
            }

            CancelSilenceTimer();
            _audioPlaybackService.Stop();
            SetTranslationPhaseStatus(StatusIdle);

            _voiceCaptureService.Stop();

            _speechToTextService?.Stop();
            ReleaseSpeechToTextService();

            var capturedText = BuildCombinedRecognizedText();
            Dispatcher.UIThread.Post(() => OriginalTextTextBox.Text = capturedText.TrimEnd());

            SaveCapturedAudioRecording();

            _phase = ListenTranslatePhase.Listening;
            _sessionInputDeviceId = null;

            StartTranslationButton.Content = "Начать перевод";
            StartTranslationButton.Background = new SolidColorBrush(Color.Parse("#16A34A"));

            _ = RunFinalTranslateAsync(capturedText);
        }

        protected override void OnClosed(EventArgs e)
        {
            CancelSilenceTimer();
            _audioPlaybackService.Stop();

            _voiceCaptureService.Stop();
            SaveCapturedAudioRecording();
            _voiceCaptureService.AudioChunkCaptured -= OnAudioChunkCaptured;
            _speechToTextService?.Stop();
            ReleaseSpeechToTextService();
            _translationService.Dispose();
            _ttsService.Dispose();
            _audioPlaybackService.Dispose();
            base.OnClosed(e);
        }

        private static string? GetVoskModelDirectoryNameForSourceLanguage(string sourceLanguageCode)
        {
            return sourceLanguageCode switch
            {
                "ru" => VoskRuModelDir,
                "en" => VoskEnModelDir,
                _ => null,
            };
        }

        private static string? FindVoskModelPathForLanguage(string sourceLanguageCode)
        {
            var mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ru"] = VoskRuModelDir,
                ["en"] = VoskEnModelDir,
                ["de"] = "vosk-model-small-de-0.15",
                ["fr"] = "vosk-model-small-fr-0.22",
                ["es"] = "vosk-model-small-es-0.42",
                ["pt"] = "vosk-model-small-pt-0.3",
            };

            if (!mapping.TryGetValue(sourceLanguageCode, out var modelDir))
            {
                return null;
            }

            return GetModelPathCandidates(modelDir).FirstOrDefault(Directory.Exists)
                   ?? Path.Combine(AppContext.BaseDirectory, "Models", modelDir);
        }

        private static string BuildModelNotFoundMessageForLanguage(string sourceLanguageCode)
        {
            var modelDir = GetVoskModelDirectoryNameForSourceLanguage(sourceLanguageCode) ?? sourceLanguageCode;
            var candidatePaths = GetModelPathCandidates(modelDir).ToList();
            var searchPaths = string.Join(Environment.NewLine, candidatePaths.Select(path => $"- {path}"));

            return $"Ошибка распознавания: модель Vosk не найдена ({modelDir}).{Environment.NewLine}" +
                   $"Положи модель в одну из папок:{Environment.NewLine}{searchPaths}{Environment.NewLine}" +
                   "Или укажи путь через переменную окружения VOSK_MODEL_PATH.";
        }

        private void ReplaceSpeechToTextService(string modelPath)
        {
            ReleaseSpeechToTextService();
            var service = new VoskRealtimeSpeechToTextService(modelPath);
            service.PartialTextUpdated += OnPartialTextUpdated;
            service.FinalTextUpdated += OnFinalTextUpdated;
            _speechToTextService = service;
        }

        private void ReleaseSpeechToTextService()
        {
            if (_speechToTextService is null)
            {
                return;
            }

            _speechToTextService.PartialTextUpdated -= OnPartialTextUpdated;
            _speechToTextService.FinalTextUpdated -= OnFinalTextUpdated;
            _speechToTextService.Dispose();
            _speechToTextService = null;
        }

        private static IEnumerable<string> GetModelPathCandidates(string modelDirectoryName)
        {
            var candidates = new List<string>();

            var fromEnvironment = Environment.GetEnvironmentVariable("VOSK_MODEL_PATH");
            if (!string.IsNullOrWhiteSpace(fromEnvironment))
            {
                candidates.Add(fromEnvironment);
            }

            candidates.Add(Path.Combine(AppContext.BaseDirectory, "Models", modelDirectoryName));
            candidates.Add(Path.Combine(AppContext.BaseDirectory, modelDirectoryName));
            candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "Models", modelDirectoryName));

            var solutionRoot = FindSolutionRoot();
            if (!string.IsNullOrWhiteSpace(solutionRoot))
            {
                candidates.Add(Path.Combine(solutionRoot, "VoiceTranslatorApp", "Models", modelDirectoryName));
                candidates.Add(Path.Combine(solutionRoot, "Models", modelDirectoryName));
            }

            return candidates.Distinct();
        }

        private static string? FindSolutionRoot()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);

            while (current is not null)
            {
                if (current.GetFiles("*.sln").Length > 0)
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            return null;
        }

        private void OnAudioChunkCaptured(object? sender, byte[] audioChunk)
        {
            if (!_isTranslationRunning)
            {
                return;
            }

            lock (_audioRecordingSync)
            {
                _sessionWaveWriter?.Write(audioChunk, 0, audioChunk.Length);
                _sessionRecordedBytes += audioChunk.Length;
            }

            // Захват и распознавание только в фазе прослушивания; во время перевода/озвучки захват остановлен.
            if (_phase != ListenTranslatePhase.Listening)
            {
                return;
            }

            try
            {
                _speechToTextService?.ProcessAudioChunk(audioChunk);
            }
            catch (Exception ex)
            {
                try
                {
                    Dispatcher.UIThread.Post(() => OriginalTextTextBox.Text = $"Ошибка распознавания (runtime): {ex.Message}");
                }
                catch
                {
                }
            }
        }

        private void OnPartialTextUpdated(object? sender, string partialText)
        {
            if (!_isTranslationRunning || _phase != ListenTranslatePhase.Listening)
            {
                return;
            }

            _recognizedPartialText = partialText;
            UpdateOriginalTextBox();
        }

        private void OnFinalTextUpdated(object? sender, string finalText)
        {
            if (!_isTranslationRunning || _phase != ListenTranslatePhase.Listening)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(finalText))
            {
                if (_recognizedFinalText.Length > 0)
                {
                    _recognizedFinalText.Append(' ');
                }

                _recognizedFinalText.Append(finalText);
            }

            _recognizedPartialText = string.Empty;
            UpdateOriginalTextBox();
        }

        private string BuildCombinedRecognizedText()
        {
            var finalText = _recognizedFinalText.ToString();
            return string.IsNullOrWhiteSpace(_recognizedPartialText)
                ? finalText
                : string.IsNullOrWhiteSpace(finalText)
                    ? _recognizedPartialText
                    : $"{finalText} {_recognizedPartialText}";
        }

        private void UpdateOriginalTextBox()
        {
            var combinedText = BuildCombinedRecognizedText();
            Dispatcher.UIThread.Post(() => OriginalTextTextBox.Text = combinedText);

            if (!_isTranslationRunning || _phase != ListenTranslatePhase.Listening)
            {
                return;
            }

            var trimmed = combinedText.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return;
            }

            _utteranceStarted = true;

            if (!string.Equals(trimmed, _lastCombinedTextForSilence, StringComparison.Ordinal))
            {
                _lastCombinedTextForSilence = trimmed;
                StartOrResetSilenceTimer();
            }
        }

        private void StartOrResetSilenceTimer()
        {
            if (!_isTranslationRunning || _phase != ListenTranslatePhase.Listening)
            {
                return;
            }

            try
            {
                _silenceCts?.Cancel();
                _silenceCts?.Dispose();
            }
            catch
            {
            }

            _silenceCts = new CancellationTokenSource();
            var token = _silenceCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(2000, token).ConfigureAwait(false);
                    if (token.IsCancellationRequested || !_isTranslationRunning)
                    {
                        return;
                    }

                    await BeginUtteranceCycleAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (_isTranslationRunning)
                        {
                            OriginalTextTextBox.Text = $"Ошибка цикла: {ex.Message}";
                        }
                    });
                    await ResumeListeningAfterCycleAsync().ConfigureAwait(false);
                }
            }, token);
        }

        private void CancelSilenceTimer()
        {
            try
            {
                _silenceCts?.Cancel();
                _silenceCts?.Dispose();
            }
            catch
            {
            }

            _silenceCts = null;
        }

        private async Task BeginUtteranceCycleAsync(CancellationToken silenceToken)
        {
            if (silenceToken.IsCancellationRequested || !_isTranslationRunning || _phase != ListenTranslatePhase.Listening)
            {
                return;
            }

            var phrase = BuildCombinedRecognizedText().Trim();

            _phase = ListenTranslatePhase.Busy;
            CancelSilenceTimer();

            SetTranslationPhaseStatus(StatusStep2Silence);
            _voiceCaptureService.Stop();

            if (!_utteranceStarted || string.IsNullOrWhiteSpace(phrase))
            {
                await ResumeListeningAfterCycleAsync().ConfigureAwait(false);
                return;
            }

            SetTranslationPhaseStatus(StatusStep3Translate);

            string translated;
            try
            {
                translated = await _translationService
                    .TranslateAsync(phrase, _sessionSourceLangCode, _sessionTargetLangCode)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    TranslatedTextTextBox.Text = $"Ошибка перевода: {ex.Message}";
                });
                SetTranslationPhaseStatus($"Ошибка перевода: {ex.Message}");
                await ResumeListeningAfterCycleAsync().ConfigureAwait(false);
                return;
            }

            if (!_isTranslationRunning)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                TranslatedTextTextBox.Text = translated;
            });

            if (string.IsNullOrWhiteSpace(translated))
            {
                await ResumeListeningAfterCycleAsync().ConfigureAwait(false);
                return;
            }

            SetTranslationPhaseStatus(StatusStep4Speak);

            byte[] wav;
            try
            {
                var voice = _sessionTargetLangCode switch
                {
                    "ru" => "alena",
                    "en" => "john",
                    _ => null,
                };

                wav = await _ttsService
                    .SynthesizeAsync(translated, _sessionTargetLangCode, voice, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                SetTranslationPhaseStatus("Ошибка синтеза речи. Возврат к распознаванию.");
                await ResumeListeningAfterCycleAsync().ConfigureAwait(false);
                return;
            }

            if (!_isTranslationRunning)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _audioPlaybackService.PlayWavBytes(wav, () =>
                {
                    Dispatcher.UIThread.Post(() => _ = ResumeListeningAfterCycleAsync());
                });
            });
        }

        private async Task ResumeListeningAfterCycleAsync()
        {
            if (!_isTranslationRunning)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _recognizedFinalText.Clear();
                _recognizedPartialText = string.Empty;
                OriginalTextTextBox.Text = string.Empty;
                TranslatedTextTextBox.Text = string.Empty;
            });

            try
            {
                _speechToTextService?.ResetRecognitionSession();
            }
            catch
            {
            }

            _phase = ListenTranslatePhase.Listening;
            _utteranceStarted = false;
            _lastCombinedTextForSilence = string.Empty;
            CancelSilenceTimer();

            SetTranslationPhaseStatus(StatusStep5And6NewCycle);

            if (!_isTranslationRunning)
            {
                return;
            }

            _voiceCaptureService.Start(_sessionInputDeviceId);
        }

        private async Task RunFinalTranslateAsync(string capturedText)
        {
            try
            {
                var trimmed = capturedText.Trim();
                if (string.IsNullOrEmpty(trimmed))
                {
                    await Dispatcher.UIThread.InvokeAsync(() => TranslatedTextTextBox.Text = string.Empty);
                    return;
                }

                try
                {
                    var translated = await _translationService
                        .TranslateAsync(trimmed, _sessionSourceLangCode, _sessionTargetLangCode)
                        .ConfigureAwait(false);

                    await Dispatcher.UIThread.InvokeAsync(() => TranslatedTextTextBox.Text = translated);
                }
                catch (Exception ex)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                        TranslatedTextTextBox.Text = $"Ошибка перевода: {ex.Message}");
                }
            }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    SetTranslationLanguageSelectorsEnabled(isEnabled: true);
                    SetTranslationPhaseStatus(StatusIdle);
                });
            }
        }

        private static string GetSelectedLanguageCode(ComboBox comboBox)
        {
            var idx = comboBox.SelectedIndex;
            if (idx >= 0 && idx < LanguageCodesByComboOrder.Length)
            {
                return LanguageCodesByComboOrder[idx];
            }

            if (comboBox.SelectedItem is ComboBoxItem item)
            {
                var display = item.Content?.ToString()?.Trim();
                if (!string.IsNullOrEmpty(display) && LanguageDisplayToCode.TryGetValue(display, out var code))
                {
                    return code;
                }
            }

            var text = comboBox.SelectionBoxItem?.ToString()?.Trim();
            if (!string.IsNullOrEmpty(text) && LanguageDisplayToCode.TryGetValue(text, out var fromBox))
            {
                return fromBox;
            }

            return "ru";
        }

        private void SetTranslationLanguageSelectorsEnabled(bool isEnabled)
        {
            SourceLanguageComboBox.IsEnabled = isEnabled;
            TargetLanguageComboBox.IsEnabled = isEnabled;
        }

        private void SaveCapturedAudioRecording()
        {
            string? recordingPath;
            long recordedBytes;

            lock (_audioRecordingSync)
            {
                recordingPath = _sessionRecordingPath;
                recordedBytes = _sessionRecordedBytes;
            }

            FinishRecordingSession();
            if (string.IsNullOrWhiteSpace(recordingPath))
            {
                return;
            }

            try
            {
                if (recordedBytes <= 0)
                {
                    if (File.Exists(recordingPath))
                    {
                        File.Delete(recordingPath);
                    }

                    return;
                }
            }
            catch
            {
            }
        }

        private void BeginRecordingSession()
        {
            lock (_audioRecordingSync)
            {
                FinishRecordingSessionUnsafe();

                try
                {
                    var recordingsDirectory = ResolveRecordingsPath();
                    Directory.CreateDirectory(recordingsDirectory);
                    _sessionRecordingPath = Path.Combine(recordingsDirectory, $"translation-{DateTime.Now:yyyyMMdd-HHmmss}.wav");
                    _sessionWaveWriter = new WaveFileWriter(_sessionRecordingPath, new WaveFormat(16000, 16, 1));
                    _sessionRecordedBytes = 0;
                }
                catch
                {
                    _sessionRecordingPath = null;
                    _sessionWaveWriter = null;
                    _sessionRecordedBytes = 0;
                }
            }
        }

        private void FinishRecordingSession()
        {
            lock (_audioRecordingSync)
            {
                FinishRecordingSessionUnsafe();
            }
        }

        private void FinishRecordingSessionUnsafe()
        {
            _sessionWaveWriter?.Dispose();
            _sessionWaveWriter = null;
        }

        private static string ResolveRecordingsPath()
        {
            var solutionRoot = FindSolutionRoot();
            if (!string.IsNullOrWhiteSpace(solutionRoot))
            {
                return Path.Combine(solutionRoot, "VoiceTranslatorApp", "Recordings");
            }

            return Path.Combine(AppContext.BaseDirectory, "Recordings");
        }
    }
}
