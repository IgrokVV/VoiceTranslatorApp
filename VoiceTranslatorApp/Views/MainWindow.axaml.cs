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
        private bool _isTranslationRunning;
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
        private int _translateDebounceNonce;
        private string _sessionSourceLangCode = "ru";
        private string _sessionTargetLangCode = "en";
        private CancellationTokenSource? _periodicTranslateCts;

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

                Interlocked.Increment(ref _translateDebounceNonce);

                _recognizedFinalText.Clear();
                _recognizedPartialText = string.Empty;
                OriginalTextTextBox.Text = string.Empty;
                TranslatedTextTextBox.Text = string.Empty;

                _sessionSourceLangCode = GetSelectedLanguageCode(SourceLanguageComboBox);
                _sessionTargetLangCode = GetSelectedLanguageCode(TargetLanguageComboBox);

                // Try to find a Vosk model folder for the selected source language.
                var modelPath = FindVoskModelPathForLanguage(_sessionSourceLangCode);
                if (modelPath is null || !Directory.Exists(modelPath))
                {
                    _isTranslationRunning = false;
                    OriginalTextTextBox.Text = BuildModelNotFoundMessageForLanguage(_sessionSourceLangCode);
                    return;
                }

                ReplaceSpeechToTextService(modelPath);
                if (_speechToTextService is null)
                {
                    _isTranslationRunning = false;
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
                    FinishRecordingSession();
                    ReleaseSpeechToTextService();
                    OriginalTextTextBox.Text = $"Ошибка распознавания: {ex.Message}";
                    return;
                }

                SetTranslationLanguageSelectorsEnabled(isEnabled: false);

                _voiceCaptureService.Start(selectedInputId);
                // Start periodic translation loop that translates accumulated text every 2 seconds.
                _periodicTranslateCts?.Cancel();
                _periodicTranslateCts = new CancellationTokenSource();
                var periodicToken = _periodicTranslateCts.Token;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (!periodicToken.IsCancellationRequested && _isTranslationRunning)
                        {
                            try
                            {
                                await Task.Delay(2000, periodicToken).ConfigureAwait(false);
                                if (periodicToken.IsCancellationRequested || !_isTranslationRunning)
                                    break;

                                var toTranslate = BuildCombinedRecognizedText().Trim();
                                if (string.IsNullOrEmpty(toTranslate))
                                    continue;

                                var translated = await _translationService
                                    .TranslateAsync(toTranslate, _sessionSourceLangCode, _sessionTargetLangCode, periodicToken)
                                    .ConfigureAwait(false);

                                await Dispatcher.UIThread.InvokeAsync(() =>
                                {
                                    if (_isTranslationRunning)
                                    {
                                        TranslatedTextTextBox.Text = translated;
                                    }
                                });
                            }
                            catch (OperationCanceledException)
                            {
                                break;
                            }
                            catch (Exception ex)
                            {
                                await Dispatcher.UIThread.InvokeAsync(() =>
                                {
                                    if (_isTranslationRunning)
                                    {
                                        TranslatedTextTextBox.Text = $"Ошибка перевода: {ex.Message}";
                                    }
                                });
                            }
                        }
                    }
                    finally
                    {
                        // no-op
                    }
                }, periodicToken);
                StartTranslationButton.Content = "Перевод идёт";
                StartTranslationButton.Background = new SolidColorBrush(Color.Parse("#DC2626"));
                return;
            }

            Interlocked.Increment(ref _translateDebounceNonce);

            _voiceCaptureService.Stop();

            // Stop periodic translation loop.
            try
            {
                _periodicTranslateCts?.Cancel();
                _periodicTranslateCts?.Dispose();
            }
            catch
            {
            }
            _periodicTranslateCts = null;

            _speechToTextService?.Stop();
            ReleaseSpeechToTextService();

            var capturedText = BuildCombinedRecognizedText();
            Dispatcher.UIThread.Post(() => OriginalTextTextBox.Text = capturedText.TrimEnd());

            SaveCapturedAudioRecording();

            StartTranslationButton.Content = "Начать перевод";
            StartTranslationButton.Background = new SolidColorBrush(Color.Parse("#16A34A"));

            _ = RunFinalTranslateAsync(capturedText);
        }

        protected override void OnClosed(EventArgs e)
        {
            Interlocked.Increment(ref _translateDebounceNonce);

            _voiceCaptureService.Stop();
            SaveCapturedAudioRecording();
            _voiceCaptureService.AudioChunkCaptured -= OnAudioChunkCaptured;
            _speechToTextService?.Stop();
            ReleaseSpeechToTextService();
            _translationService.Dispose();
            base.OnClosed(e);
        }

        private string BuildModelNotFoundMessage(string modelDirectoryName)
        {
            var candidatePaths = GetModelPathCandidates(modelDirectoryName).ToList();
            var searchPaths = string.Join(Environment.NewLine, candidatePaths.Select(path => $"- {path}"));

            return $"Ошибка распознавания: модель Vosk не найдена ({modelDirectoryName}).{Environment.NewLine}" +
                   $"Положи модель в одну из папок:{Environment.NewLine}{searchPaths}{Environment.NewLine}" +
                   "Или укажи путь через переменную окружения VOSK_MODEL_PATH.";
        }

        private string ResolveVoskModelPath(string modelDirectoryName)
        {
            return GetModelPathCandidates(modelDirectoryName).FirstOrDefault(Directory.Exists)
                   ?? Path.Combine(AppContext.BaseDirectory, "Models", modelDirectoryName);
        }

        private static string? GetVoskModelDirectoryNameForSourceLanguage(string sourceLanguageCode)
        {
            // Kept for backward-compatibility but not used directly anymore.
            return sourceLanguageCode switch
            {
                "ru" => VoskRuModelDir,
                "en" => VoskEnModelDir,
                _ => null,
            };
        }

        private static string? FindVoskModelPathForLanguage(string sourceLanguageCode)
        {
            // Map language codes to candidate model folder names. Add any extra
            // models you place under the Models/ directory here.
            var mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ru"] = VoskRuModelDir,
                ["en"] = VoskEnModelDir,
                // Add mappings for additional models placed under Models/.
                ["de"] = "vosk-model-small-de-0.15",
                ["fr"] = "vosk-model-small-fr-0.22",
                // Spanish model in repository is 0.42
                ["es"] = "vosk-model-small-es-0.42",
                // Portuguese model
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
            lock (_audioRecordingSync)
            {
                _sessionWaveWriter?.Write(audioChunk, 0, audioChunk.Length);
                _sessionRecordedBytes += audioChunk.Length;
            }

            _speechToTextService?.ProcessAudioChunk(audioChunk);
        }

        private void OnPartialTextUpdated(object? sender, string partialText)
        {
            _recognizedPartialText = partialText;
            UpdateOriginalTextBox();
        }

        private void OnFinalTextUpdated(object? sender, string finalText)
        {
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
            ScheduleDebouncedTranslation(combinedText.Trim());
        }

        private void ScheduleDebouncedTranslation(string trimmedForTranslate)
        {
            if (!_isTranslationRunning)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(trimmedForTranslate))
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_isTranslationRunning)
                    {
                        TranslatedTextTextBox.Text = string.Empty;
                    }
                });
                return;
            }

            var nonce = Interlocked.Increment(ref _translateDebounceNonce);
            var source = _sessionSourceLangCode;
            var target = _sessionTargetLangCode;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(400).ConfigureAwait(false);
                    if (nonce != Volatile.Read(ref _translateDebounceNonce) || !_isTranslationRunning)
                    {
                        return;
                    }

                    var translated = await _translationService
                        .TranslateAsync(trimmedForTranslate, source, target)
                        .ConfigureAwait(false);

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (!_isTranslationRunning || nonce != Volatile.Read(ref _translateDebounceNonce))
                        {
                            return;
                        }

                        TranslatedTextTextBox.Text = translated;
                    });
                }
                catch (Exception ex)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (!_isTranslationRunning || nonce != Volatile.Read(ref _translateDebounceNonce))
                        {
                            return;
                        }

                        TranslatedTextTextBox.Text = $"Ошибка перевода: {ex.Message}";
                    });
                }
            });
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
                await Dispatcher.UIThread.InvokeAsync(() => SetTranslationLanguageSelectorsEnabled(isEnabled: true));
            }
        }

        private static string GetSelectedLanguageCode(ComboBox comboBox)
        {
            var idx = comboBox.SelectedIndex;
            if (idx >= 0 && idx < LanguageCodesByComboOrder.Length)
            {
                return LanguageCodesByComboOrder[idx];
            }

            // Avalonia не всегда отдаёт SelectedItem как ComboBoxItem — пробуем по подписи.
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
                // Ignore save errors to keep translation flow uninterrupted.
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