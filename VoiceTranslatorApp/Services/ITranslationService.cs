using System;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceTranslatorApp.Services
{
    public interface ITranslationService : IDisposable
    {
        Task<string> TranslateAsync(string text, string sourceLanguageCode, string targetLanguageCode, CancellationToken cancellationToken = default);
    }
}
