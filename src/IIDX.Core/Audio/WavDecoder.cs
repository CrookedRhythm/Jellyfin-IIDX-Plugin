using System.Buffers.Binary;

namespace IIDX.Core.Audio;

/// <summary>Decoded audio: interleaved stereo float samples at <see cref="WavDecoder.SampleRate"/>.</summary>
public sealed class AudioClip
{
    public AudioClip(float[] samples)
    {
        Samples = samples;
    }

    public static AudioClip Empty { get; } = new(Array.Empty<float>());

    public float[] Samples { get; }

    public int Frames => Samples.Length / 2;
}

/// <summary>
/// In-process decoder for the RIFF/WAVE flavours found in .2dx archives: PCM (8/16/24/32 bit),
/// IEEE float and Microsoft ADPCM. Anything else returns null so the caller can fall back to ffmpeg.
/// </summary>
public static class WavDecoder
{
    public const int SampleRate = 44100;

    private static readonly int[] AdaptationTable = [230, 230, 230, 230, 307, 409, 512, 614, 768, 614, 512, 409, 307, 230, 230, 230];

    private static readonly (int C1, int C2)[] DefaultCoefs = [(256, 0), (512, -256), (0, 0), (192, 64), (240, 0), (460, -208), (392, -232)];

    public static AudioClip? TryDecode(byte[] wav)
    {
        if (wav.Length < 12 || !wav.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !wav.AsSpan(8, 4).SequenceEqual("WAVE"u8))
        {
            return null;
        }

        ReadOnlySpan<byte> fmt = default;
        ReadOnlySpan<byte> data = default;
        bool haveFmt = false, haveData = false;
        int pos = 12;
        while (pos + 8 <= wav.Length)
        {
            var id = wav.AsSpan(pos, 4);
            int size = checked((int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(pos + 4)), (uint)(wav.Length - pos - 8)));
            var body = wav.AsSpan(pos + 8, size);
            if (id.SequenceEqual("fmt "u8))
            {
                fmt = body;
                haveFmt = true;
            }
            else if (id.SequenceEqual("data"u8))
            {
                data = body;
                haveData = true;
            }

            pos += 8 + size + (size & 1);
        }

        if (!haveFmt || !haveData || fmt.Length < 16)
        {
            return null;
        }

        int format = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
        int channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);
        int rate = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(fmt[4..]));
        int blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(fmt[12..]);
        int bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);
        if (format == 0xFFFE && fmt.Length >= 40)
        {
            // WAVE_FORMAT_EXTENSIBLE: the real format tag is the first two bytes of the sub-format GUID.
            format = BinaryPrimitives.ReadUInt16LittleEndian(fmt[24..]);
        }

        if (channels is < 1 or > 2 || rate <= 0)
        {
            return null;
        }

        float[]? samples = format switch
        {
            1 => DecodePcm(data, channels, bits),
            3 when bits == 32 => DecodeFloat(data, channels),
            2 => DecodeMsAdpcm(data, fmt, channels, blockAlign),
            _ => null,
        };

        if (samples == null)
        {
            return null;
        }

        var stereo = channels == 2 ? samples : ToStereo(samples);
        return new AudioClip(rate == SampleRate ? stereo : Resample(stereo, rate, SampleRate));
    }

    private static float[]? DecodePcm(ReadOnlySpan<byte> data, int channels, int bits)
    {
        int bytes = bits / 8;
        if (bytes is < 1 or > 4 || bits % 8 != 0)
        {
            return null;
        }

        int count = data.Length / bytes / channels * channels;
        var output = new float[count];
        for (int i = 0; i < count; i++)
        {
            var s = data.Slice(i * bytes, bytes);
            output[i] = bytes switch
            {
                1 => (s[0] - 128) / 128f,
                2 => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768f,
                3 => ((s[0] | (s[1] << 8) | ((sbyte)s[2] << 16)) / 8388608f),
                _ => BinaryPrimitives.ReadInt32LittleEndian(s) / 2147483648f,
            };
        }

        return output;
    }

    private static float[] DecodeFloat(ReadOnlySpan<byte> data, int channels)
    {
        int count = data.Length / 4 / channels * channels;
        var output = new float[count];
        for (int i = 0; i < count; i++)
        {
            output[i] = BinaryPrimitives.ReadSingleLittleEndian(data[(i * 4)..]);
        }

        return output;
    }

    private static float[]? DecodeMsAdpcm(ReadOnlySpan<byte> data, ReadOnlySpan<byte> fmt, int channels, int blockAlign)
    {
        if (blockAlign <= 0)
        {
            return null;
        }

        var coefs = DefaultCoefs;
        if (fmt.Length >= 22)
        {
            // fmt extension: cbSize, samplesPerBlock, numCoef, then numCoef (c1, c2) pairs.
            int numCoef = BinaryPrimitives.ReadUInt16LittleEndian(fmt[20..]);
            if (numCoef > 0 && fmt.Length >= 22 + (numCoef * 4))
            {
                coefs = new (int, int)[numCoef];
                for (int i = 0; i < numCoef; i++)
                {
                    coefs[i] = (BinaryPrimitives.ReadInt16LittleEndian(fmt[(22 + (i * 4))..]), BinaryPrimitives.ReadInt16LittleEndian(fmt[(24 + (i * 4))..]));
                }
            }
        }

        int headerBytes = 7 * channels;
        var output = new List<float>(data.Length * 2);
        Span<int> c1 = stackalloc int[2];
        Span<int> c2 = stackalloc int[2];
        Span<int> delta = stackalloc int[2];
        Span<int> s1 = stackalloc int[2];
        Span<int> s2 = stackalloc int[2];

        for (int blockStart = 0; blockStart + headerBytes <= data.Length; blockStart += blockAlign)
        {
            var block = data.Slice(blockStart, Math.Min(blockAlign, data.Length - blockStart));
            int p = 0;
            for (int ch = 0; ch < channels; ch++)
            {
                int predictor = Math.Min(block[p++], coefs.Length - 1);
                c1[ch] = coefs[predictor].C1;
                c2[ch] = coefs[predictor].C2;
            }

            for (int ch = 0; ch < channels; ch++, p += 2)
            {
                delta[ch] = BinaryPrimitives.ReadInt16LittleEndian(block[p..]);
            }

            for (int ch = 0; ch < channels; ch++, p += 2)
            {
                s1[ch] = BinaryPrimitives.ReadInt16LittleEndian(block[p..]);
            }

            for (int ch = 0; ch < channels; ch++, p += 2)
            {
                s2[ch] = BinaryPrimitives.ReadInt16LittleEndian(block[p..]);
            }

            for (int ch = 0; ch < channels; ch++)
            {
                output.Add(s2[ch] / 32768f);
            }

            for (int ch = 0; ch < channels; ch++)
            {
                output.Add(s1[ch] / 32768f);
            }

            int nib = 0;
            for (; p < block.Length; p++)
            {
                for (int half = 0; half < 2; half++, nib++)
                {
                    int ch = channels == 2 ? nib & 1 : 0;
                    int code = half == 0 ? block[p] >> 4 : block[p] & 0x0F;
                    int signed = code >= 8 ? code - 16 : code;
                    int predicted = ((s1[ch] * c1[ch]) + (s2[ch] * c2[ch])) >> 8;
                    predicted += signed * delta[ch];
                    predicted = Math.Clamp(predicted, short.MinValue, short.MaxValue);
                    s2[ch] = s1[ch];
                    s1[ch] = predicted;
                    delta[ch] = (AdaptationTable[code] * delta[ch]) >> 8;
                    if (delta[ch] < 16)
                    {
                        delta[ch] = 16;
                    }

                    output.Add(predicted / 32768f);
                }
            }
        }

        if (channels == 2 && output.Count % 2 == 1)
        {
            output.RemoveAt(output.Count - 1);
        }

        return output.ToArray();
    }

    internal static float[] ToStereo(float[] mono)
    {
        var output = new float[mono.Length * 2];
        for (int i = 0; i < mono.Length; i++)
        {
            output[2 * i] = mono[i];
            output[(2 * i) + 1] = mono[i];
        }

        return output;
    }

    /// <summary>Linear interpolation resampler for interleaved stereo audio.</summary>
    internal static float[] Resample(float[] stereo, int fromRate, int toRate)
    {
        int inFrames = stereo.Length / 2;
        if (inFrames == 0)
        {
            return stereo;
        }

        long outFrames = (long)inFrames * toRate / fromRate;
        var output = new float[outFrames * 2];
        double step = (double)fromRate / toRate;
        for (long i = 0; i < outFrames; i++)
        {
            double src = i * step;
            int i0 = (int)src;
            int i1 = Math.Min(i0 + 1, inFrames - 1);
            float t = (float)(src - i0);
            output[2 * i] = stereo[2 * i0] + ((stereo[2 * i1] - stereo[2 * i0]) * t);
            output[(2 * i) + 1] = stereo[(2 * i0) + 1] + ((stereo[(2 * i1) + 1] - stereo[(2 * i0) + 1]) * t);
        }

        return output;
    }
}
