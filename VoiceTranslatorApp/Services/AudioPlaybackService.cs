using System;
using System.IO;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace VoiceTranslatorApp.Services
{
    /// <summary>Воспроизведение WAV на выбранное устройство вывода (WASAPI) или устройство по умолчанию.</summary>
    public sealed class AudioPlaybackService : IDisposable
    {
        private IWavePlayer? _player;
        private WaveFileReader? _reader;
        private MemoryStream? _stream;
        private Action? _currentOnFinished;

        /// <param name="outputDeviceFriendlyName">Имя устройства из списка вывода Windows; null — устройство по умолчанию.</param>
        public void PlayWavBytes(byte[] wavData, Action? onFinished = null, string? outputDeviceFriendlyName = null)
        {
            Stop();

            if (wavData is null || wavData.Length == 0)
            {
                try
                {
                    onFinished?.Invoke();
                }
                catch
                {
                }

                return;
            }

            try
            {
                _stream = new MemoryStream(wavData);
                _reader = new WaveFileReader(_stream);
                var device = ResolveRenderDevice(outputDeviceFriendlyName);
                _player = new WasapiOut(device, AudioClientShareMode.Shared, true, 200);
                _player.PlaybackStopped += OnPlaybackStopped;
                _player.Init(_reader);
                _currentOnFinished = onFinished;
                _player.Play();
            }
            catch
            {
                CleanupCurrent();
                try
                {
                    onFinished?.Invoke();
                }
                catch
                {
                }
            }
        }

        public void Stop()
        {
            try
            {
                _player?.Stop();
            }
            catch
            {
            }

            CleanupCurrent();
        }

        private static MMDevice ResolveRenderDevice(string? outputDeviceFriendlyName)
        {
            using var enumerator = new MMDeviceEnumerator();

            if (!string.IsNullOrWhiteSpace(outputDeviceFriendlyName))
            {
                var match = enumerator
                    .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                    .FirstOrDefault(d =>
                        string.Equals(d.FriendlyName, outputDeviceFriendlyName, StringComparison.OrdinalIgnoreCase));

                if (match is not null)
                {
                    return match;
                }
            }

            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }

        private void CleanupCurrent()
        {
            if (_player is not null)
            {
                try
                {
                    _player.PlaybackStopped -= OnPlaybackStopped;
                }
                catch
                {
                }

                try
                {
                    _player.Dispose();
                }
                catch
                {
                }

                _player = null;
            }

            try
            {
                _reader?.Dispose();
            }
            catch
            {
            }

            _reader = null;

            try
            {
                _stream?.Dispose();
            }
            catch
            {
            }

            _stream = null;
        }

        private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
        {
            var callback = _currentOnFinished;
            _currentOnFinished = null;
            CleanupCurrent();

            try
            {
                callback?.Invoke();
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
