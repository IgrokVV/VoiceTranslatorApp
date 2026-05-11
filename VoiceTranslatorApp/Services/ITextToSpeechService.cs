using System.Threading;
using System.Threading.Tasks;

namespace VoiceTranslatorApp.Services
{
    public interface ITextToSpeechService : System.IDisposable
    {
        /// <summary>
        /// Возвращает WAV-данные (байты). languageCode — например "ru-RU" или "en-US" в зависимости от голоса.
        /// options — пользовательские настройки/«модель голоса» (опционально).
        /// </summary>
        Task<byte[]> SynthesizeAsync(
            string text,
            string languageCode,
            TextToSpeechSynthesisOptions? options = null,
            CancellationToken cancellationToken = default);
    }
}
