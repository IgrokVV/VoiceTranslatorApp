using System;

namespace VoiceTranslatorApp.Services
{
    /// <summary>
    /// Пресет озвучки: скорость на стороне Yandex + тон/громкость локально после синтеза.
    /// </summary>
    public sealed record TextToSpeechVoiceModel
    {
        public required string Name { get; init; }

        /// <summary>Скорость речи Yandex TTS (обычно 0.1..3.0; по умолчанию 1.0).</summary>
        public double Speed { get; init; } = 1.0;

        /// <summary>Сдвиг высоты в полутонах (−6..+6), применяется к WAV на устройстве.</summary>
        public double PitchSemitones { get; init; }

        /// <summary>Линейная громкость (0.25..2.0), по умолчанию 1.0.</summary>
        public double LinearGain { get; init; } = 1.0;

        public override string ToString() => Name;
    }

    public sealed record TextToSpeechSynthesisOptions
    {
        public TextToSpeechVoiceModel? Model { get; init; }

        /// <summary>Частота дискретизации ответа. 16000 — безопасный дефолт.</summary>
        public int SampleRateHertz { get; init; } = 16000;

        public static TextToSpeechSynthesisOptions Default { get; } = new TextToSpeechSynthesisOptions();
    }
}

