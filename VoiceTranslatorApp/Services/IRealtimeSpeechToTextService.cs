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
        void Stop();
    }
}
