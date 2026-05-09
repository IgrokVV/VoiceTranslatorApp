using System;
using System.IO;
using System.Collections.Generic;
using NAudio.Wave;

namespace VoiceTranslatorApp.Services
{
    public sealed class AudioPlaybackService : IDisposable
    {
        private readonly object _sync = new();
        private readonly Queue<(byte[] Data, Action? OnFinished)> _queue = new();

        private WaveOutEvent? _player;
        private WaveFileReader? _reader;
        private MemoryStream? _stream;
        private bool _isPlaying;

        public void PlayWavBytes(byte[] wavData, Action? onFinished = null)
        {
            if (wavData is null || wavData.Length == 0)
                return;

            lock (_sync)
            {
                _queue.Enqueue((wavData, onFinished));
                if (_isPlaying)
                {
                    // already playing — just queue
                    return;
                }

                _isPlaying = true;
            }

            StartNext();
        }

        private void StartNext()
        {
            byte[]? data = null;
            Action? onFinished = null;
            lock (_sync)
            {
                if (_queue.Count == 0)
                {
                    _isPlaying = false;
                    return;
                }

                var item = _queue.Dequeue();
                data = item.Data;
                onFinished = item.OnFinished;
            }

            try
            {
                _stream = new MemoryStream(data);
                _reader = new WaveFileReader(_stream);
                _player = new WaveOutEvent();
                _player.PlaybackStopped += OnPlaybackStopped;
                _player.Init(_reader);
                _player.Play();
                // store continuation on the player tag so OnPlaybackStopped can invoke it
                _player?.GetType();
                _currentOnFinished = onFinished;
            }
            catch
            {
                // If playback fails for this item, clean up and continue with next.
                CleanupCurrent();
                // Continue with next item.
                StartNext();
            }
        }

        public void Stop()
        {
            lock (_sync)
            {
                _queue.Clear();
            }

            try
            {
                _player?.Stop();
            }
            catch { }

            CleanupCurrent();
        }

        private Action? _currentOnFinished;

        private void CleanupCurrent()
        {
            if (_player is not null)
            {
                try { _player.PlaybackStopped -= OnPlaybackStopped; } catch { }
                try { _player.Dispose(); } catch { }
                _player = null;
            }

            try { _reader?.Dispose(); } catch { }
            _reader = null;

            try { _stream?.Dispose(); } catch { }
            _stream = null;
        }

        private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
        {
            // capture and clear current callback
            var callback = _currentOnFinished;
            _currentOnFinished = null;

            CleanupCurrent();

            try
            {
                callback?.Invoke();
            }
            catch { }

            // Start next queued item, if any.
            StartNext();
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
