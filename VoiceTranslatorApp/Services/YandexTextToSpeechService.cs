using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceTranslatorApp.Services
{
    public sealed class YandexTextToSpeechService : ITextToSpeechService
    {
        private const string DefaultEndpoint = "https://tts.api.cloud.yandex.net/speech/v1/tts:synthesize";
        private readonly HttpClient _http;
        private readonly bool _ownsHttp;
        private readonly string? _apiKey;
        private bool _disposed;

        public YandexTextToSpeechService(HttpClient? httpClient = null)
        {
            if (httpClient is not null)
            {
                _http = httpClient;
                _ownsHttp = false;
            }
            else
            {
                _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                _ownsHttp = true;
            }

            _apiKey = ResolveApiKey();
        }

        public async Task<byte[]> SynthesizeAsync(
            string text,
            string languageCode,
            TextToSpeechSynthesisOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return Array.Empty<byte>();
            }

            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                throw new InvalidOperationException("Yandex TTS: отсутствует API key. Установите переменную YANDEX_API_KEY / YC_API_KEY или заполните YandexTranslateLocal.ApiKey.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, DefaultEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Api-Key", _apiKey);

            // Yandex TTS may not accept "wav" format parameter in this endpoint for some configurations.
            // Request raw PCM (lpcm) and wrap into WAV container if necessary.
            options ??= TextToSpeechSynthesisOptions.Default;
            var sampleRate = options.SampleRateHertz <= 0 ? 16000 : options.SampleRateHertz;

            var form = new Dictionary<string, string>
            {
                ["text"] = text,
                ["lang"] = languageCode,
                ["format"] = "lpcm",
                ["sampleRateHertz"] = sampleRate.ToString(CultureInfo.InvariantCulture),
            };

            var model = options.Model;

            if (model is not null && model.Speed > 0 && Math.Abs(model.Speed - 1.0) > 0.0001)
            {
                form["speed"] = model.Speed.ToString("0.0##", CultureInfo.InvariantCulture);
            }

            request.Content = new FormUrlEncodedContent(form);

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new HttpRequestException($"Yandex TTS HTTP {(int)response.StatusCode}: {body}");
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            // If response already contains WAV (RIFF header), return as-is.
            if (bytes.Length >= 4 && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F')
            {
                if (model is not null)
                {
                    return TtsWavPostProcessor.Apply(bytes, model.PitchSemitones, model.LinearGain);
                }

                return bytes;
            }

            // Otherwise assume raw PCM (16-bit little-endian) and wrap into WAV container with requested sample rate.
            try
            {
                var wav = CreateWavFromPcm(bytes, sampleRateHertz: sampleRate, bitsPerSample: 16, channels: 1);
                if (model is not null)
                {
                    return TtsWavPostProcessor.Apply(wav, model.PitchSemitones, model.LinearGain);
                }

                return wav;
            }
            catch
            {
                // If wrapping fails, return raw bytes as fallback.
                return bytes;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_ownsHttp) _http.Dispose();
        }

        private static string? ResolveApiKey()
        {
            try
            {
                var fromLocal = YandexTranslateLocal.ApiKey;
                if (!string.IsNullOrWhiteSpace(fromLocal))
                {
                    var t = fromLocal.Trim();
                    if (t.Length > 0 && !IsPlaceholder(t))
                        return t;
                }
            }
            catch
            {
            }

            var fromEnv = FirstNonEmpty(
                Environment.GetEnvironmentVariable("YANDEX_API_KEY"),
                Environment.GetEnvironmentVariable("YC_API_KEY"));

            return fromEnv;
        }

        private static string? FirstNonEmpty(params string?[] values)
        {
            foreach (var v in values)
            {
                if (!string.IsNullOrWhiteSpace(v)) return v!.Trim();
            }

            return null;
        }

        private static bool IsPlaceholder(string trimmed)
        {
            if (trimmed.Length == 0) return true;
            if (trimmed.StartsWith('<')) return true;
            if (trimmed.Contains("YOUR_API_KEY", StringComparison.OrdinalIgnoreCase)) return true;
            if (trimmed.Equals("PASTE_SERVICE_ACCOUNT_SECRET_KEY", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static byte[] CreateWavFromPcm(byte[] pcmData, int sampleRateHertz, int bitsPerSample, int channels)
        {
            using var ms = new System.IO.MemoryStream();
            using var writer = new System.IO.BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);

            int byteRate = sampleRateHertz * channels * bitsPerSample / 8;
            short blockAlign = (short)(channels * bitsPerSample / 8);

            // RIFF header
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            writer.Write((int)(36 + pcmData.Length)); // file size - 8
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));

            // fmt chunk
            writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16); // PCM chunk size
            writer.Write((short)1); // audio format = PCM
            writer.Write((short)channels);
            writer.Write(sampleRateHertz);
            writer.Write(byteRate);
            writer.Write(blockAlign);
            writer.Write((short)bitsPerSample);

            // data chunk
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            writer.Write(pcmData.Length);
            writer.Write(pcmData);

            writer.Flush();
            return ms.ToArray();
        }
    }
}
