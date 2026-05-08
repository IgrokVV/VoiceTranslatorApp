using System;
using System.IO;
using NAudio.Wave;

namespace VoiceTranslatorApp.Services
{
    public sealed class AudioPlaybackService : IDisposable
    {
        private WaveOutEvent? _player;
        private WaveFileReader? _reader;
        private MemoryStream? _stream;

        public void PlayWavBytes(byte[] wavData)
        {
            Stop();

            _stream = new MemoryStream(wavData);
            _reader = new WaveFileReader(_stream);
            _player = new WaveOutEvent();
            _player.PlaybackStopped += OnPlaybackStopped;
            _player.Init(_reader);
            _player.Play();
        }

        public void Stop()
        {
            try
            {
                _player?.Stop();
            }
            catch { }

            if (_player is not null)
            {
                _player.PlaybackStopped -= OnPlaybackStopped;
                _player.Dispose();
                _player = null;
            }

            _reader?.Dispose();
            _reader = null;

            _stream?.Dispose();
            _stream = null;
        }

        private void OnPlaybackStopped(object? sender, StoppedEventArgs e) => Stop();

        public void Dispose() => Stop();
    }
}
