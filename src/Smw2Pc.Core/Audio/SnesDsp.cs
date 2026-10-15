namespace Smw2Pc.Core.Audio;

internal sealed class SnesDsp
{
    private const int VoiceCount = 8;
    private const int VoiceRegisterStride = 0x10;
    private const int BrrBlockBytes = 9;
    private const int BrrSamplesPerBlock = 16;
    private const int PitchUnit = 0x1000;
    private static readonly ushort[] EnvelopePeriods =
    [
        0, 2048, 1536, 1280, 1024, 768, 640, 512,
        384, 320, 256, 192, 160, 128, 96, 80,
        64, 48, 40, 32, 24, 20, 16, 12,
        10, 8, 6, 5, 4, 3, 2, 1,
    ];

    private readonly byte[] registers = new byte[0x80];
    private readonly Voice[] voices = Enumerable.Range(0, VoiceCount).Select(_ => new Voice()).ToArray();
    private readonly int[] echoFirLeft = new int[8];
    private readonly int[] echoFirRight = new int[8];
    private int pendingKeyOn;
    private int latchedKeyOn;
    private int noiseLfsr = 0x4000;
    private int envelopeClock;
    private int echoPosition;
    private int echoLength;
    private int echoHistoryIndex;

    private readonly byte[] audioRam;

    public SnesDsp(byte[] audioRam)
    {
        this.audioRam = audioRam ?? throw new ArgumentNullException(nameof(audioRam));
        registers[0x6C] = 0xE0;
    }

    public byte Read(byte address) => registers[address & 0x7F];

    public void Write(byte address, byte value)
    {
        var register = address & 0x7F;
        registers[register] = value;
        if (register == 0x4C)
        {
            pendingKeyOn = value;
        }
        else if (register == 0x7C)
        {
            registers[register] = 0;
        }
    }

    public void Reset()
    {
        Array.Clear(registers);
        registers[0x6C] = 0xE0;
        foreach (var voice in voices)
        {
            voice.Reset();
        }

        Array.Clear(echoFirLeft);
        Array.Clear(echoFirRight);
        pendingKeyOn = 0;
        latchedKeyOn = 0;
        noiseLfsr = 0x4000;
        envelopeClock = 0;
        echoPosition = 0;
        echoLength = 0;
        echoHistoryIndex = 0;
    }

    public (short Left, short Right) RenderSample()
    {
        var flags = registers[0x6C];
        envelopeClock = (envelopeClock + 1) % 30720;
        var noiseRate = flags & 0x1F;
        if (RateReached(noiseRate))
        {
            var feedback = ((noiseLfsr >> 1) ^ noiseLfsr) & 1;
            noiseLfsr = (noiseLfsr >> 1) | (feedback << 14);
        }

        var keyOn = latchedKeyOn;
        latchedKeyOn = pendingKeyOn;
        pendingKeyOn = 0;

        var mainLeft = 0;
        var mainRight = 0;
        var echoInputLeft = 0;
        var echoInputRight = 0;

        for (var index = 0; index < VoiceCount; index++)
        {
            var voice = voices[index];
            var baseAddress = index * VoiceRegisterStride;
            var bit = 1 << index;
            if ((keyOn & bit) != 0)
            {
                voice.BeginKeyOn();
            }

            if ((flags & 0x80) != 0 || (registers[0x5C] & bit) != 0)
            {
                voice.Release();
                if ((flags & 0x80) != 0)
                {
                    voice.Envelope = 0;
                }
            }

            if (voice.KeyOnDelay > 0)
            {
                if (voice.KeyOnDelay == 5)
                {
                    StartVoice(voice, baseAddress, index);
                }

                voice.Envelope = 0;
                voice.HiddenEnvelope = 0;
                voice.KeyOnDelay--;
                registers[baseAddress + 8] = 0;
                registers[baseAddress + 9] = 0;
                voice.Output = 0;
                continue;
            }

            var sample = (registers[0x3D] & bit) != 0
                ? SignExtend15(noiseLfsr << 1)
                : Interpolate(voice);
            AdvanceEnvelope(voice, baseAddress);
            sample = (sample * voice.Envelope) >> 11;
            sample &= ~1;
            voice.Output = sample;
            registers[baseAddress + 8] = (byte)(voice.Envelope >> 4);
            registers[baseAddress + 9] = (byte)(sample >> 8);

            var leftVolume = (sbyte)registers[baseAddress];
            var rightVolume = (sbyte)registers[baseAddress + 1];
            var voiceLeft = (sample * leftVolume) >> 7;
            var voiceRight = (sample * rightVolume) >> 7;
            mainLeft = Clamp16(mainLeft + voiceLeft);
            mainRight = Clamp16(mainRight + voiceRight);

            if ((registers[0x4D] & bit) != 0)
            {
                echoInputLeft = Clamp16(echoInputLeft + voiceLeft);
                echoInputRight = Clamp16(echoInputRight + voiceRight);
            }

            var pitch = registers[baseAddress + 2] | ((registers[baseAddress + 3] & 0x3F) << 8);
            if (index > 0 && (registers[0x2D] & bit) != 0)
            {
                pitch = Math.Clamp(pitch + (((voices[index - 1].Output >> 5) * pitch) >> 10), 0, 0x3FFF);
            }

            AdvanceVoiceSample(voice, pitch, index);
        }

        var (echoLeft, echoRight) = ProcessEcho(echoInputLeft, echoInputRight, flags);
        var mixedLeft = Clamp16(((mainLeft * (sbyte)registers[0x0C]) >> 7) + ((echoLeft * (sbyte)registers[0x2C]) >> 7));
        var mixedRight = Clamp16(((mainRight * (sbyte)registers[0x1C]) >> 7) + ((echoRight * (sbyte)registers[0x3C]) >> 7));
        if ((flags & 0x40) != 0)
        {
            mixedLeft = 0;
            mixedRight = 0;
        }

        return ((short)mixedLeft, (short)mixedRight);
    }

    private void StartVoice(Voice voice, int registerBase, int index)
    {
        var sampleDirectory = registers[0x5D] << 8;
        var sampleNumber = registers[registerBase + 4];
        var directoryEntry = (sampleDirectory + sampleNumber * 4) & 0xFFFF;
        voice.BrrAddress = audioRam[directoryEntry] | (audioRam[(directoryEntry + 1) & 0xFFFF] << 8);
        voice.LoopAddress = audioRam[(directoryEntry + 2) & 0xFFFF] | (audioRam[(directoryEntry + 3) & 0xFFFF] << 8);
        voice.SampleIndex = 0;
        voice.Phase = 0;
        voice.FilterHistoryOne = 0;
        voice.FilterHistoryTwo = 0;
        voice.Ended = false;
        voice.DecodeBlock(audioRam);
        registers[0x7C] &= (byte)~(1 << index);
    }

    private int Interpolate(Voice voice)
    {
        if (voice.Ended || voice.DecodedSamples.Length == 0)
        {
            return 0;
        }

        var current = voice.DecodedSamples[voice.SampleIndex];
        var next = voice.DecodedSamples[Math.Min(voice.SampleIndex + 1, BrrSamplesPerBlock - 1)];
        return current + (((next - current) * voice.Phase) >> 12);
    }

    private void AdvanceVoiceSample(Voice voice, int pitch, int index)
    {
        voice.Phase += pitch;
        while (voice.Phase >= PitchUnit && !voice.Ended)
        {
            voice.Phase -= PitchUnit;
            voice.SampleIndex++;
            if (voice.SampleIndex >= BrrSamplesPerBlock)
            {
                voice.SampleIndex = 0;
                voice.AdvanceBlock(audioRam, out var ended, out var reachedEnd);
                if (reachedEnd)
                {
                    registers[0x7C] |= (byte)(1 << index);
                }

                if (ended)
                {
                    voice.Release();
                }
            }
        }
    }

    private void AdvanceEnvelope(Voice voice, int registerBase)
    {
        if (voice.IsReleased)
        {
            voice.Envelope = Math.Max(0, voice.Envelope - 8);
            return;
        }

        var adsr1 = registers[registerBase + 5];
        var adsr2 = registers[registerBase + 6];
        var gain = registers[registerBase + 7];
        if ((adsr1 & 0x80) != 0)
        {
            if (voice.EnvelopeMode == EnvelopeMode.Attack)
            {
                var attackRate = (adsr1 & 0x0F) * 2 + 1;
                if (RateReached(attackRate))
                {
                    voice.HiddenEnvelope = Math.Min(0x7FF, voice.HiddenEnvelope + (attackRate == 31 ? 0x400 : 0x20));
                    voice.Envelope = voice.HiddenEnvelope;
                    if (voice.HiddenEnvelope == 0x7FF)
                    {
                        voice.EnvelopeMode = EnvelopeMode.Decay;
                    }
                }

                return;
            }

            var sustainLevel = ((adsr1 >> 5) + 1) * 0x100;
            var envelopeRate = voice.EnvelopeMode == EnvelopeMode.Decay
                ? ((adsr1 >> 3) & 0x0E) + 0x10
                : adsr2 & 0x1F;
            if (RateReached(envelopeRate))
            {
                voice.HiddenEnvelope = Math.Max(0, voice.HiddenEnvelope - 1 - (voice.HiddenEnvelope >> 8));
                voice.Envelope = voice.HiddenEnvelope;
                if (voice.EnvelopeMode == EnvelopeMode.Decay && voice.HiddenEnvelope <= sustainLevel)
                {
                    voice.EnvelopeMode = EnvelopeMode.Sustain;
                }
            }

            return;
        }

        var mode = gain >> 5;
        if (mode < 4)
        {
            voice.Envelope = (gain & 0x7F) << 4;
            voice.HiddenEnvelope = voice.Envelope;
            return;
        }

        var gainRate = gain & 0x1F;
        if (!RateReached(gainRate))
        {
            return;
        }

        var step = mode switch
        {
            4 => -0x20,
            5 => -1 - (voice.HiddenEnvelope >> 8),
            6 => 0x20,
            _ => voice.HiddenEnvelope < 0x600 ? 0x20 : 0x08,
        };
        voice.HiddenEnvelope = Math.Clamp(voice.HiddenEnvelope + step, 0, 0x7FF);
        voice.Envelope = voice.HiddenEnvelope;
    }

    private bool RateReached(int rate)
    {
        return rate > 0 && envelopeClock % EnvelopePeriods[rate] == 0;
    }

    private (int Left, int Right) ProcessEcho(int inputLeft, int inputRight, byte flags)
    {
        if (echoPosition == 0)
        {
            echoLength = (registers[0x7D] & 0x0F) * 0x800;
        }

        var baseAddress = registers[0x6D] << 8;
        var address = (baseAddress + echoPosition) & 0xFFFF;
        var echoInputL = (short)(audioRam[address] | (audioRam[(address + 1) & 0xFFFF] << 8));
        var echoInputR = (short)(audioRam[(address + 2) & 0xFFFF] | (audioRam[(address + 3) & 0xFFFF] << 8));
        echoHistoryIndex = (echoHistoryIndex + 1) & 7;
        echoFirLeft[echoHistoryIndex] = echoInputL >> 1;
        echoFirRight[echoHistoryIndex] = echoInputR >> 1;

        var filteredLeft = 0;
        var filteredRight = 0;
        for (var tap = 0; tap < 8; tap++)
        {
            var coefficient = (sbyte)registers[(tap << 4) | 0x0F];
            var historyIndex = (echoHistoryIndex + 1 + tap) & 7;
            filteredLeft += (echoFirLeft[historyIndex] * coefficient) >> 6;
            filteredRight += (echoFirRight[historyIndex] * coefficient) >> 6;
        }

        filteredLeft = Clamp16(filteredLeft);
        filteredRight = Clamp16(filteredRight);
        if ((flags & 0x20) == 0)
        {
            var feedback = (sbyte)registers[0x0D];
            var writeLeft = (short)Clamp16(inputLeft + ((filteredLeft * feedback) >> 7));
            var writeRight = (short)Clamp16(inputRight + ((filteredRight * feedback) >> 7));
            audioRam[address] = (byte)writeLeft;
            audioRam[(address + 1) & 0xFFFF] = (byte)(writeLeft >> 8);
            audioRam[(address + 2) & 0xFFFF] = (byte)writeRight;
            audioRam[(address + 3) & 0xFFFF] = (byte)(writeRight >> 8);
        }

        echoPosition += 4;
        if (echoLength == 0 || echoPosition >= echoLength)
        {
            echoPosition = 0;
        }

        return (filteredLeft, filteredRight);
    }

    private static int Clamp16(int value) => Math.Clamp(value, short.MinValue, short.MaxValue);

    private static int SignExtend15(int value)
    {
        value &= 0x7FFF;
        return (value & 0x4000) != 0 ? value - 0x8000 : value;
    }

    private sealed class Voice
    {
        public readonly int[] DecodedSamples = new int[BrrSamplesPerBlock];
        public int BrrAddress;
        public int LoopAddress;
        public int SampleIndex;
        public int Phase;
        public int Envelope;
        public int HiddenEnvelope;
        public int KeyOnDelay;
        public int Output;
        public int FilterHistoryOne;
        public int FilterHistoryTwo;
        public EnvelopeMode EnvelopeMode;
        public bool IsReleased;
        public bool Ended;

        public void Reset()
        {
            Array.Clear(DecodedSamples);
            BrrAddress = LoopAddress = SampleIndex = Phase = Envelope = HiddenEnvelope = KeyOnDelay = Output = 0;
            FilterHistoryOne = FilterHistoryTwo = 0;
            EnvelopeMode = EnvelopeMode.Release;
            IsReleased = true;
            Ended = false;
        }

        public void BeginKeyOn()
        {
            KeyOnDelay = 5;
            Envelope = 0;
            HiddenEnvelope = 0;
            IsReleased = false;
            EnvelopeMode = EnvelopeMode.Attack;
        }

        public void Release()
        {
            IsReleased = true;
            EnvelopeMode = EnvelopeMode.Release;
        }

        public void DecodeBlock(byte[] ram)
        {
            if (Ended)
            {
                Array.Clear(DecodedSamples);
                return;
            }

            var header = ram[BrrAddress];
            var shift = header >> 4;
            var filter = (header >> 2) & 3;
            for (var sampleIndex = 0; sampleIndex < BrrSamplesPerBlock; sampleIndex++)
            {
                var packed = ram[(BrrAddress + 1 + sampleIndex / 2) & 0xFFFF];
                var nibble = (sampleIndex & 1) == 0 ? packed >> 4 : packed & 0x0F;
                if ((nibble & 8) != 0)
                {
                    nibble -= 16;
                }

                var sample = shift <= 12
                    ? (nibble << shift) >> 1
                    : (nibble < 0 ? -2048 : 0);

                sample = filter switch
                {
                    1 => sample + FilterHistoryOne - (FilterHistoryOne >> 4),
                    2 => sample + ((FilterHistoryOne * 61) >> 5) - ((FilterHistoryTwo * 15) >> 4),
                    3 => sample + ((FilterHistoryOne * 115) >> 6) - ((FilterHistoryTwo * 13) >> 4),
                    _ => sample,
                };
                sample = SignExtend15(Clamp16(sample) * 2);
                FilterHistoryTwo = FilterHistoryOne >> 1;
                FilterHistoryOne = sample;
                DecodedSamples[sampleIndex] = sample;
            }
        }

        public void AdvanceBlock(byte[] ram, out bool ended, out bool reachedEnd)
        {
            var header = ram[BrrAddress];
            reachedEnd = (header & 1) != 0;
            ended = reachedEnd && (header & 2) == 0;
            if (ended)
            {
                Ended = true;
                Array.Clear(DecodedSamples);
                return;
            }

            BrrAddress = reachedEnd ? LoopAddress : (BrrAddress + BrrBlockBytes) & 0xFFFF;
            DecodeBlock(ram);
        }
    }

    private enum EnvelopeMode
    {
        Release,
        Attack,
        Decay,
        Sustain,
    }
}
