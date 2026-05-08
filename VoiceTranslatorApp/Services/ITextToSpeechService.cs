using System.Threading;
using System.Threading.Tasks;

namespace VoiceTranslatorApp.Services
{
    public interface ITextToSpeechService : System.IDisposable
    {
        /// <summary>
        /// Возвращает WAV-данные (байты). languageCode — например "ru-RU" или "en-US" в зависимости от голоса.
        /// voiceName — необязательно.
        /// </summary>
        Task<byte[]> SynthesizeAsync(string text, string languageCode, string? voiceName = null, CancellationToken cancellationToken = default);
    }
}
