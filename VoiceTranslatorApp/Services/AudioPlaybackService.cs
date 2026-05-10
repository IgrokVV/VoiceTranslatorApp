using System;
using System.IO;
using NAudio.Wave;

namespace VoiceTranslatorApp.Services
{
    /// <summary>???? ??????????????? WAV ??? ???????: ????? ????? ????????????? ???????.</summary>
    public sealed class AudioPlaybackService : IDisposable
    {
        private WaveOutEvent? _player;
        private WaveFileReader? _reader;
        private MemoryStream? _stream;
        private Action? _currentOnFinished;

        public void PlayWavBytes(byte[] wavData, Action? onFinished = null)
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
                _player = new WaveOutEvent();
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
