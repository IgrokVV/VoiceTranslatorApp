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
using VoiceTranslatorApp.Services;

namespace VoiceTranslatorApp.Views
{
    public partial class MainWindow : Window
    {
        private bool _isTranslationRunning;
        private const string VoskModelDirectoryName = "vosk-model-small-ru-0.22";
        private readonly IVoiceCaptureService _voiceCaptureService = new VoiceCaptureService();
        private readonly IRealtimeSpeechToTextService _speechToTextService;
        private readonly string _voskModelPath;
        private readonly Dictionary<string, string> _inputDevices = [];
        private readonly StringBuilder _recognizedFinalText = new();
        private readonly object _audioRecordingSync = new();
        private string _recognizedPartialText = string.Empty;
        private WaveFileWriter? _sessionWaveWriter;
        private string? _sessionRecordingPath;
        private long _sessionRecordedBytes;
        private string? _lastSavedRecordingPath;

        public MainWindow()
        {
            InitializeComponent();
            _voskModelPath = ResolveVoskModelPath();
            _speechToTextService = new VoskRealtimeSpeechToTextService(_voskModelPath);
            _voiceCaptureService.AudioChunkCaptured += OnAudioChunkCaptured;
            _speechToTextService.PartialTextUpdated += OnPartialTextUpdated;
            _speechToTextService.FinalTextUpdated += OnFinalTextUpdated;
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

                _recognizedFinalText.Clear();
                _recognizedPartialText = string.Empty;
                OriginalTextTextBox.Text = string.Empty;
                _lastSavedRecordingPath = null;
                if (!Directory.Exists(_voskModelPath))
                {
                    _isTranslationRunning = false;
                    OriginalTextTextBox.Text = BuildModelNotFoundMessage();
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
                    OriginalTextTextBox.Text = $"Ошибка распознавания: {ex.Message}";
                    return;
                }
                _voiceCaptureService.Start(selectedInputId);
                StartTranslationButton.Content = "Перевод идёт";
                StartTranslationButton.Background = new SolidColorBrush(Color.Parse("#DC2626"));
                return;
            }

            _voiceCaptureService.Stop();
            _speechToTextService.Stop();
            SaveCapturedAudioRecording();
            StartTranslationButton.Content = "Начать перевод";
            StartTranslationButton.Background = new SolidColorBrush(Color.Parse("#16A34A"));
            UpdateOriginalTextBox();
        }

        protected override void OnClosed(EventArgs e)
        {
            _voiceCaptureService.Stop();
            SaveCapturedAudioRecording();
            _voiceCaptureService.AudioChunkCaptured -= OnAudioChunkCaptured;
            _speechToTextService.PartialTextUpdated -= OnPartialTextUpdated;
            _speechToTextService.FinalTextUpdated -= OnFinalTextUpdated;
            _speechToTextService.Dispose();
            base.OnClosed(e);
        }

        private string BuildModelNotFoundMessage()
        {
            var candidatePaths = GetModelPathCandidates().ToList();
            var searchPaths = string.Join(Environment.NewLine, candidatePaths.Select(path => $"- {path}"));

            return $"Ошибка распознавания: модель Vosk не найдена ({VoskModelDirectoryName}).{Environment.NewLine}" +
                   $"Положи модель в одну из папок:{Environment.NewLine}{searchPaths}{Environment.NewLine}" +
                   "Или укажи путь через переменную окружения VOSK_MODEL_PATH.";
        }

        private string ResolveVoskModelPath()
        {
            return GetModelPathCandidates().FirstOrDefault(Directory.Exists)
                   ?? Path.Combine(AppContext.BaseDirectory, "Models", VoskModelDirectoryName);
        }

        private static IEnumerable<string> GetModelPathCandidates()
        {
            var candidates = new List<string>();

            var fromEnvironment = Environment.GetEnvironmentVariable("VOSK_MODEL_PATH");
            if (!string.IsNullOrWhiteSpace(fromEnvironment))
            {
                candidates.Add(fromEnvironment);
            }

            candidates.Add(Path.Combine(AppContext.BaseDirectory, "Models", VoskModelDirectoryName));
            candidates.Add(Path.Combine(AppContext.BaseDirectory, VoskModelDirectoryName));
            candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "Models", VoskModelDirectoryName));

            var solutionRoot = FindSolutionRoot();
            if (!string.IsNullOrWhiteSpace(solutionRoot))
            {
                candidates.Add(Path.Combine(solutionRoot, "VoiceTranslatorApp", "Models", VoskModelDirectoryName));
                candidates.Add(Path.Combine(solutionRoot, "Models", VoskModelDirectoryName));
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

            _speechToTextService.ProcessAudioChunk(audioChunk);
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

        private void UpdateOriginalTextBox()
        {
            var finalText = _recognizedFinalText.ToString();
            var combinedText = string.IsNullOrWhiteSpace(_recognizedPartialText)
                ? finalText
                : string.IsNullOrWhiteSpace(finalText)
                    ? _recognizedPartialText
                    : $"{finalText} {_recognizedPartialText}";

            if (!_isTranslationRunning && !string.IsNullOrWhiteSpace(_lastSavedRecordingPath))
            {
                combinedText = string.IsNullOrWhiteSpace(combinedText)
                    ? $"Аудио сохранено: {_lastSavedRecordingPath}"
                    : $"{combinedText}{Environment.NewLine}{Environment.NewLine}Аудио сохранено: {_lastSavedRecordingPath}";
            }

            Dispatcher.UIThread.Post(() => OriginalTextTextBox.Text = combinedText);
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
                _lastSavedRecordingPath = "ошибка сохранения (путь к файлу не был создан)";
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

                    _lastSavedRecordingPath = "запись пустая (данные с микрофона не поступили)";
                    return;
                }

                var fileInfo = new FileInfo(recordingPath);
                _lastSavedRecordingPath = fileInfo.Length <= 44
                    ? "запись пустая (получен только WAV-заголовок)"
                    : fileInfo.FullName;
            }
            catch (Exception ex)
            {
                _lastSavedRecordingPath = $"ошибка сохранения ({ex.Message})";
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
                catch (Exception ex)
                {
                    _sessionRecordingPath = null;
                    _sessionWaveWriter = null;
                    _sessionRecordedBytes = 0;
                    _lastSavedRecordingPath = $"ошибка подготовки записи ({ex.Message})";
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
            var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrWhiteSpace(documentsPath))
            {
                return Path.Combine(documentsPath, "VoiceTranslatorApp", "Recordings");
            }

            return Path.Combine(AppContext.BaseDirectory, "Recordings");
        }
    }
}