using System;
using NAudio.Wave;

namespace VoiceTranslatorApp.Services
{
    public sealed class VoiceCaptureService : IVoiceCaptureService, IDisposable
    {
        private IWaveIn? _capture;

        public event EventHandler<byte[]>? AudioChunkCaptured;

        public bool IsCapturing { get; private set; }

        public void Start(string? inputDeviceId = null)
        {
            if (IsCapturing)
            {
                return;
            }

            _capture = CreateCapture(inputDeviceId);
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            _capture.StartRecording();
            IsCapturing = true;
        }

        public void Stop()
        {
            if (!IsCapturing)
            {
                return;
            }

            _capture?.StopRecording();
            ReleaseCapture();
            IsCapturing = false;
        }

        public void Dispose()
        {
            Stop();
        }

        private static IWaveIn CreateCapture(string? inputDeviceId)
        {
            var capture = new WaveInEvent
            {
                WaveFormat = new WaveFormat(16000, 1),
                BufferMilliseconds = 100
            };

            if (string.IsNullOrWhiteSpace(inputDeviceId))
            {
                return capture;
            }

            // We use WaveInEvent for consistent PCM 16k mono chunks required by Vosk.
            // inputDeviceId currently contains friendly device name from UI mapping.
            var requestedName = inputDeviceId.Trim();
            for (var i = 0; i < WaveInEvent.DeviceCount; i++)
            {
                var capabilities = WaveInEvent.GetCapabilities(i);
                if (capabilities.ProductName.Contains(requestedName, StringComparison.OrdinalIgnoreCase))
                {
                    capture.DeviceNumber = i;
                    break;
                }
            }

            return capture;
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded <= 0)
            {
                return;
            }

            var audioChunk = new byte[e.BytesRecorded];
            Array.Copy(e.Buffer, audioChunk, e.BytesRecorded);
            AudioChunkCaptured?.Invoke(this, audioChunk);
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            ReleaseCapture();
            IsCapturing = false;
        }

        private void ReleaseCapture()
        {
            if (_capture is null)
            {
                return;
            }

            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            _capture.Dispose();
            _capture = null;
        }
    }
}
