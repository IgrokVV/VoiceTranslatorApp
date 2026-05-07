using System;
using VoiceTranslatorApp;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceTranslatorApp.Services
{
    /// <summary>
    /// Yandex Cloud Translate API v2 (<see href="https://cloud.yandex.com/en/docs/translate/">docs</see>).
    /// Credentials: переменные окружения <c>YANDEX_FOLDER_ID</c> / <c>YC_FOLDER_ID</c> и
    /// <c>YANDEX_API_KEY</c> / <c>YC_API_KEY</c>,
    /// либо JSON-файл <c>yandex.translate.local.json</c> (см. <see cref="GetCredentialHintLocations"/>).
    /// </summary>
    public sealed class YandexTranslateService : ITranslationService, IDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };

        private readonly HttpClient _http;
        private readonly bool _ownsHttpClient;
        private readonly string? _folderId;
        private readonly string? _apiKey;
        private bool _disposed;

        public YandexTranslateService(HttpClient? httpClient = null)
        {
            if (httpClient is not null)
            {
                _http = httpClient;
                _ownsHttpClient = false;
            }
            else
            {
                _http = new HttpClient
                {
                    BaseAddress = new Uri("https://translate.api.cloud.yandex.net/translate/v2/"),
                    Timeout = TimeSpan.FromSeconds(30),
                };
                _ownsHttpClient = true;
            }

            (_folderId, _apiKey) = ResolveCredentials();
        }

        public async Task<string> TranslateAsync(
            string text,
            string sourceLanguageCode,
            string targetLanguageCode,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            if (string.Equals(sourceLanguageCode, targetLanguageCode, StringComparison.OrdinalIgnoreCase))
            {
                return text;
            }

            if (string.IsNullOrWhiteSpace(_folderId) || string.IsNullOrWhiteSpace(_apiKey))
            {
                throw new InvalidOperationException(BuildMissingCredentialsMessage());
            }

            var payload = new TranslateRequest
            {
                FolderId = _folderId,
                Texts = new List<string> { text },
                SourceLanguageCode = sourceLanguageCode,
                TargetLanguageCode = targetLanguageCode,
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, "translate");
            request.Headers.Authorization = new AuthenticationHeaderValue("Api-Key", _apiKey);
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Yandex Translate HTTP {(int)response.StatusCode}: {body}");
            }

            var parsed = JsonSerializer.Deserialize<TranslateResponse>(body, JsonOptions);
            var first = parsed?.Translations is { Count: > 0 } ? parsed.Translations[0].Text : null;
            return string.IsNullOrEmpty(first) ? text : first!;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_ownsHttpClient)
            {
                _http.Dispose();
            }
        }

        public static string GetCredentialHintLocations()
            => FormatPrimaryPathsHumanReadable();

        private static string FormatPrimaryPathsHumanReadable()
        {
            const string Name = "yandex.translate.local.json";
            try
            {
                var exe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, Name));
                var cwd = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, Name));
                var roaming = Path.GetFullPath(
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "VoiceTranslatorApp",
                        Name));
                return $"{exe}; рабочая папка процесса ({cwd}); или {roaming}";
            }
            catch
            {
                return "файл «yandex.translate.local.json»: рядом с exe / в текущей папке процесса / в %AppData%\\VoiceTranslatorApp\\";
            }
        }

        private static string BuildMissingCredentialsMessage()
        {
            var sb = new StringBuilder();
            sb.Append("Нет ключей для Yandex Cloud Translate.").Append(Environment.NewLine);
            sb.Append("Сделайте одно из двух:").Append(Environment.NewLine);
            sb.Append(
                    " • задайте на уровне пользователя или системы переменные YANDEX_FOLDER_ID и YANDEX_API_KEY (ещё поддерживаются YC_FOLDER_ID, YC_API_KEY), перезапустите приложение;")
                .Append(Environment.NewLine);
            sb.Append(" • создайте «yandex.translate.local.json» (содержание — см. образец из выходной папки) в одном из каталогов ниже")
                .Append(Environment.NewLine).Append(Environment.NewLine);
            sb.Append(BuildCredentialFilesystemDiagnostics());

            sb.Append(Environment.NewLine).Append(Environment.NewLine);
            sb.Append("Частые ошибки: файл лежит в папке проекта или репозитория, но не там, где .exe после сборки; либо в JSON оставлены строки-заглушки из example.");

            return sb.ToString();
        }

        private static string BuildCredentialFilesystemDiagnostics()
        {
            var sb = new StringBuilder();
            var i = 0;
            foreach (var line in DescribeCredentialCandidates())
            {
                i++;
                sb.Append(i).Append(". ").Append(line).Append(Environment.NewLine);
            }

            return sb.ToString().TrimEnd();
        }

        private static IEnumerable<string> DescribeCredentialCandidates()
        {
            foreach (var path in BuildCredentialCandidates())
            {
                if (!File.Exists(path))
                {
                    yield return $"файл не найден: {path}";
                    continue;
                }

                string status;
                try
                {
                    var text = File.ReadAllText(path);
                    var secrets = JsonSerializer.Deserialize<LocalYandexSecrets>(text, JsonOptions);
                    var f = NormalizeYandexField(secrets?.FolderId, isFolderId: true);
                    var k = NormalizeYandexField(secrets?.ApiKey, isFolderId: false);
                    if (f is null && k is null)
                    {
                        status = "JSON прочитан, но folderId и apiKey пустые или совпадают с заглушками из example — подставьте реальные значения.";
                    }
                    else if (f is null)
                    {
                        status = "нет корректного folderId (пусто или заглушка).";
                    }
                    else if (k is null)
                    {
                        status = "нет корректного apiKey (пусто или заглушка).";
                    }
                    else
                    {
                        status = "поля выглядят заполненными; если ошибка сохранится, проверьте значения на cloud.yandex.ru.";
                    }
                }
                catch (Exception ex)
                {
                    status = $"файл есть, но чтение/JSON ошибка: {ex.Message}";
                }

                yield return $"{path}: {status}";
            }
        }

        private static IReadOnlyList<string> BuildCredentialCandidates()
        {
            const string Name = "yandex.translate.local.json";
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var paths = new List<string>();

            void AddCandidate(string candidate)
            {
                try
                {
                    var full = Path.GetFullPath(candidate);

                    if (full.Length <= 248 && seen.Add(full))
                    {
                        paths.Add(full);
                    }
                }
                catch
                {
                }
            }

            AddCandidate(Path.Combine(AppContext.BaseDirectory, Name));

            try
            {
                AddCandidate(Path.Combine(Environment.CurrentDirectory, Name));
            }
            catch
            {
            }

            try
            {
                var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                AddCandidate(Path.Combine(roaming, "VoiceTranslatorApp", Name));
            }
            catch
            {
            }

            try
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                for (var depth = 0; depth < 8 && dir is not null; depth++)
                {
                    AddCandidate(Path.Combine(dir.FullName, Name));
                    dir = dir.Parent;
                }
            }
            catch
            {
            }

            return paths;
        }

        private static (string? folderId, string? apiKey) ResolveCredentials()
        {
            // Only use the local C# file (YandexTranslateLocal) and environment variables.
            // Do not read any JSON files. This ensures credentials come from the
            // explicit local source or from environment variables.
            string? folderId = NormalizeYandexField(YandexTranslateLocal.FolderId, isFolderId: true);
            string? apiKey = NormalizeYandexField(YandexTranslateLocal.ApiKey, isFolderId: false);

            folderId ??= FirstNonEmpty(
                Environment.GetEnvironmentVariable("YANDEX_FOLDER_ID"),
                Environment.GetEnvironmentVariable("YC_FOLDER_ID"));

            apiKey ??= FirstNonEmpty(
                Environment.GetEnvironmentVariable("YANDEX_API_KEY"),
                Environment.GetEnvironmentVariable("YC_API_KEY"));

            return (folderId, apiKey);
        }

        private static bool IsPlaceholderValue(string trimmed, bool isFolderId)
        {
            if (trimmed.Length == 0)
            {
                return true;
            }

            if (trimmed.StartsWith('<') || trimmed.Contains("YOUR_API_KEY", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (isFolderId)
            {
                return trimmed.Equals("PASTE_YANDEX_CLOUD_FOLDER_ID", StringComparison.OrdinalIgnoreCase)
                       || trimmed.Contains("YOUR_FOLDER", StringComparison.OrdinalIgnoreCase)
                       || trimmed.Contains("<folder", StringComparison.OrdinalIgnoreCase);
            }

            return trimmed.Equals("PASTE_SERVICE_ACCOUNT_SECRET_KEY", StringComparison.OrdinalIgnoreCase);
        }

        private static string? NormalizeYandexField(string? value, bool isFolderId)
        {
            var n = NormalizeSecret(value);
            if (n is null)
            {
                return null;
            }

            return IsPlaceholderValue(n, isFolderId) ? null : n;
        }

        private static string? NormalizeSecret(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var t = value.Trim();
            return t.Length > 0 ? t : null;
        }

        private static string? FirstNonEmpty(params string?[] values)
        {
            foreach (var v in values)
            {
                var n = NormalizeSecret(v);
                if (n is not null)
                {
                    return n;
                }
            }

            return null;
        }

        private sealed class LocalYandexSecrets
        {
            public string? FolderId { get ; set; } = "b1gum8oejc2u2l9lfm13";
            public string? ApiKey { get; set; } = "AQVNxBYLY9fO1VLwGZFJfDFolpp1n1qWadkhlJki";
        }

        private sealed class TranslateRequest
        {
            public string FolderId { get; set; } = "";
            public List<string> Texts { get; set; } = [];
            public string SourceLanguageCode { get; set; } = "";
            public string TargetLanguageCode { get; set; } = "";
        }

        private sealed class TranslateResponse
        {
            public List<TranslationEntry>? Translations { get; set; }
        }

        private sealed class TranslationEntry
        {
            public string? Text { get; set; }
        }
    }
}
