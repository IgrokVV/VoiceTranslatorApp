using System;

namespace VoiceTranslatorApp.Services
{
    public interface IRealtimeSpeechToTextService : IDisposable
    {
        event EventHandler<string>? PartialTextUpdated;
        event EventHandler<string>? FinalTextUpdated;

        bool IsRunning { get; }

        void Start();
        void ProcessAudioChunk(byte[] audioChunk);

        /// <summary>Сбрасывает состояние распознавателя без выгрузки модели (новая фраза).</summary>
        void ResetRecognitionSession();

        void Stop();
    }
}
