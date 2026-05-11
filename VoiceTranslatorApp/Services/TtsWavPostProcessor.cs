using System;
using System.IO;

namespace VoiceTranslatorApp.Services
{
    /// <summary>
    /// Локальная обработка PCM после Yandex TTS: сдвиг высоты (полутоны) и громкость.
    /// Поддерживается только WAV mono 16-bit LE (как в ответе сервиса после обёртки).
    /// </summary>
    public static class TtsWavPostProcessor
    {
        public static byte[] Apply(byte[] wavBytes, double pitchSemitones, double linearGain)
        {
            if (wavBytes.Length < 44)
            {
                return wavBytes;
            }

            if (Math.Abs(pitchSemitones) < 0.01 && Math.Abs(linearGain - 1.0) < 0.001)
            {
                return wavBytes;
            }

            if (!TryExtractMonoPcm16(wavBytes, out var sampleRate, out var pcm))
            {
                return wavBytes;
            }

            var afterPitch = Math.Abs(pitchSemitones) < 0.01 ? pcm : ResamplePitchSemitones(pcm, pitchSemitones);
            ApplyLinearGain(afterPitch, linearGain);
            return BuildWavMono16Le(sampleRate, afterPitch);
        }

        private static void ApplyLinearGain(short[] samples, double linearGain)
        {
            if (Math.Abs(linearGain - 1.0) < 0.001)
            {
                return;
            }

            for (var i = 0; i < samples.Length; i++)
            {
                var v = Math.Round(samples[i] * linearGain);
                samples[i] = (short)Math.Clamp(v, short.MinValue, short.MaxValue);
            }
        }

        private static short[] ResamplePitchSemitones(short[] input, double semitones)
        {
            var ratio = Math.Pow(2.0, semitones / 12.0);
            var outLen = Math.Max(1, (int)(input.Length / ratio));
            var output = new short[outLen];

            for (var i = 0; i < outLen; i++)
            {
                var srcPos = i * ratio;
                var i0 = (int)Math.Floor(srcPos);
                var frac = srcPos - i0;
                if (i0 >= input.Length - 1)
                {
                    output[i] = input[^1];
                }
                else
                {
                    var s0 = input[i0];
                    var s1 = input[i0 + 1];
                    var v = s0 + (s1 - s0) * frac;
                    output[i] = (short)Math.Clamp(Math.Round(v), short.MinValue, short.MaxValue);
                }
            }

            return output;
        }

        private static bool TryExtractMonoPcm16(byte[] wav, out int sampleRate, out short[] samples)
        {
            sampleRate = 0;
            samples = Array.Empty<short>();

            try
            {
                using var ms = new MemoryStream(wav, writable: false);
                using var br = new BinaryReader(ms);

                if (br.ReadUInt32() != FourCc("RIFF"))
                {
                    return false;
                }

                _ = br.ReadUInt32(); // riff chunk size
                if (br.ReadUInt32() != FourCc("WAVE"))
                {
                    return false;
                }

                ushort audioFormat = 0;
                ushort numChannels = 0;
                uint bitsPerSample = 0;
                var foundFmt = false;
                byte[]? pcmBytes = null;

                while (ms.Position + 8 <= ms.Length)
                {
                    var id = br.ReadUInt32();
                    var size = br.ReadInt32();
                    var data = br.ReadBytes(size);
                    if (data.Length < size)
                    {
                        return false;
                    }

                    if ((size & 1) == 1 && ms.Position < ms.Length)
                    {
                        _ = br.ReadByte(); // padding
                    }

                    if (id == FourCc("fmt "))
                    {
                        using var fmtMs = new MemoryStream(data, writable: false);
                        using var fmtBr = new BinaryReader(fmtMs);
                        audioFormat = fmtBr.ReadUInt16();
                        numChannels = fmtBr.ReadUInt16();
                        sampleRate = fmtBr.ReadInt32();
                        _ = fmtBr.ReadUInt32(); // byte rate
                        _ = fmtBr.ReadUInt16(); // block align
                        bitsPerSample = fmtBr.ReadUInt16();
                        foundFmt = true;
                    }
                    else if (id == FourCc("data"))
                    {
                        pcmBytes = data;
                    }
                }

                if (!foundFmt || pcmBytes is null || pcmBytes.Length < 2)
                {
                    return false;
                }

                if (audioFormat != 1 || numChannels != 1 || bitsPerSample != 16)
                {
                    return false;
                }

                var n = pcmBytes.Length / 2;
                var arr = new short[n];
                Buffer.BlockCopy(pcmBytes, 0, arr, 0, pcmBytes.Length);
                samples = arr;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static byte[] BuildWavMono16Le(int sampleRateHertz, short[] pcmSamples)
        {
            var pcmBytes = new byte[pcmSamples.Length * 2];
            Buffer.BlockCopy(pcmSamples, 0, pcmBytes, 0, pcmBytes.Length);

            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            var byteRate = sampleRateHertz * 2;
            const short blockAlign = 2;

            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + pcmBytes.Length);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(sampleRateHertz);
            writer.Write(byteRate);
            writer.Write(blockAlign);
            writer.Write((short)16);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            writer.Write(pcmBytes.Length);
            writer.Write(pcmBytes);
            writer.Flush();
            return ms.ToArray();
        }

        private static uint FourCc(string s)
        {
            if (s.Length != 4)
            {
                throw new ArgumentException(nameof(s));
            }

            return (uint)((s[0]) | (s[1] << 8) | (s[2] << 16) | (s[3] << 24));
        }
    }
}
