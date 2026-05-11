using System;
using System.Threading;
using NAudio.Wave;

namespace VoiceTranslatorApp.Services
{
    public sealed class VoiceCaptureService : IVoiceCaptureService, IDisposable
    {
        private readonly object _sync = new();
        private IWaveIn? _capture;

        public event EventHandler<byte[]>? AudioChunkCaptured;

        public bool IsCapturing { get; private set; }

        public void Start(string? inputDeviceId = null)
        {
            lock (_sync)
            {
                if (IsCapturing || _capture is not null)
                {
                    return;
                }

                _capture = CreateCapture(inputDeviceId);
                _capture.DataAvailable += OnDataAvailable;
                _capture.RecordingStopped += OnRecordingStopped;
                _capture.StartRecording();
                IsCapturing = true;
            }
        }

        public void Stop()
        {
            IWaveIn? cap;
            lock (_sync)
            {
                if (!IsCapturing || _capture is null)
                {
                    return;
                }

                cap = _capture;
            }

            try
            {
                cap.StopRecording();
            }
            catch
            {
                lock (_sync)
                {
                    ReleaseCaptureLocked();
                }
            }
        }

        public void Dispose()
        {
            Stop();

            for (var i = 0; i < 250; i++)
            {
                lock (_sync)
                {
                    if (_capture is null)
                    {
                        return;
                    }
                }

                Thread.Sleep(20);
            }

            lock (_sync)
            {
                ReleaseCaptureLocked();
            }
        }

        private static IWaveIn CreateCapture(string? inputDeviceId)
        {
            var capture = new WaveInEvent
            {
                WaveFormat = new WaveFormat(16000, 1),
                BufferMilliseconds = 100,
            };

            if (string.IsNullOrWhiteSpace(inputDeviceId))
            {
                return capture;
            }

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

            var n = e.BytesRecorded;
            var audioChunk = new byte[n];
            Array.Copy(e.Buffer, audioChunk, n);

            lock (_sync)
            {
                if (_capture is null || !IsCapturing)
                {
                    return;
                }
            }

            AudioChunkCaptured?.Invoke(this, audioChunk);
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            lock (_sync)
            {
                ReleaseCaptureLocked();
            }
        }

        private void ReleaseCaptureLocked()
        {
            if (_capture is null)
            {
                return;
            }

            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            try
            {
                _capture.Dispose();
            }
            catch
            {
            }

            _capture = null;
            IsCapturing = false;
        }
    }
}
