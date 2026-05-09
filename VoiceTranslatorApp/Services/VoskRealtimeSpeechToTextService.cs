using System;
using System.IO;
using System.Text.Json;
using Vosk;

namespace VoiceTranslatorApp.Services
{
    public sealed class VoskRealtimeSpeechToTextService : IRealtimeSpeechToTextService
    {
        private readonly object _sync = new();
        private readonly string _modelPath;

        private Model? _model;
        private VoskRecognizer? _recognizer;

        public event EventHandler<string>? PartialTextUpdated;
        public event EventHandler<string>? FinalTextUpdated;

        public bool IsRunning { get; private set; }

        public VoskRealtimeSpeechToTextService(string modelPath)
        {
            _modelPath = modelPath;
        }

        public void Start()
        {
            lock (_sync)
            {
                if (IsRunning)
                {
                    return;
                }

                if (!Directory.Exists(_modelPath))
                {
                    throw new DirectoryNotFoundException($"Vosk model not found: {_modelPath}");
                }

                Vosk.Vosk.SetLogLevel(0);
                _model = new Model(_modelPath);
                _recognizer = new VoskRecognizer(_model, 16000.0f);
                IsRunning = true;
            }
        }

        public void ProcessAudioChunk(byte[] audioChunk)
        {
            if (audioChunk.Length == 0)
            {
                return;
            }

            lock (_sync)
            {
                if (!IsRunning || _recognizer is null)
                {
                    return;
                }

                var hasFinalResult = _recognizer.AcceptWaveform(audioChunk, audioChunk.Length);
                if (hasFinalResult)
                {
                    var finalText = ExtractText(_recognizer.Result(), "text");
                    if (!string.IsNullOrWhiteSpace(finalText))
                    {
                        FinalTextUpdated?.Invoke(this, finalText);
                    }
                }
                else
                {
                    var partialText = ExtractText(_recognizer.PartialResult(), "partial");
                    PartialTextUpdated?.Invoke(this, partialText);
                }
            }
        }

        public void Stop()
        {
            lock (_sync)
            {
                if (!IsRunning)
                {
                    return;
                }

                if (_recognizer is not null)
                {
                    var tailText = ExtractText(_recognizer.FinalResult(), "text");
                    if (!string.IsNullOrWhiteSpace(tailText))
                    {
                        FinalTextUpdated?.Invoke(this, tailText);
                    }
                }

                _recognizer?.Dispose();
                _recognizer = null;

                _model?.Dispose();
                _model = null;

                IsRunning = false;
            }
        }

        public void Dispose()
        {
            Stop();
        }

        private static string ExtractText(string json, string key)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return string.Empty;
            }

            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.TryGetProperty(key, out var valueElement))
                {
                    return valueElement.GetString() ?? string.Empty;
                }
            }
            catch
            {
                // If parsing fails, don't throw — treat as no text.
            }

            return string.Empty;
        }
    }
}
