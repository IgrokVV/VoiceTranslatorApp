using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace VoiceTranslatorApp.Services
{
    public sealed class TextToSpeechVoiceModelStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        public string StorePath { get; }

        public TextToSpeechVoiceModelStore(string? storePath = null)
        {
            StorePath = storePath ?? GetDefaultStorePath();
        }

        public IReadOnlyList<TextToSpeechVoiceModel> Load()
        {
            try
            {
                if (!File.Exists(StorePath))
                {
                    return Array.Empty<TextToSpeechVoiceModel>();
                }

                var json = File.ReadAllText(StorePath);
                var models = JsonSerializer.Deserialize<List<TextToSpeechVoiceModel>>(json, JsonOptions);
                return models ?? new List<TextToSpeechVoiceModel>();
            }
            catch
            {
                return Array.Empty<TextToSpeechVoiceModel>();
            }
        }

        public void Save(IReadOnlyList<TextToSpeechVoiceModel> models)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);

            var json = JsonSerializer.Serialize(models, JsonOptions);
            File.WriteAllText(StorePath, json);
        }

        private static string GetDefaultStorePath()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appData, "VoiceTranslatorApp");
            return Path.Combine(dir, "tts-voice-models.json");
        }
    }
}

