using System;

namespace VoiceTranslatorApp.Services
{
    public interface IVoiceCaptureService
    {
        event EventHandler<byte[]>? AudioChunkCaptured;

        bool IsCapturing { get; }

        void Start(string? inputDeviceId = null);

        void Stop();
    }
}
