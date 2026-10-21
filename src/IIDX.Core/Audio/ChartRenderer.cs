using System.Buffers.Binary;
using IIDX.Core.Formats;

namespace IIDX.Core.Audio;

public sealed class RenderOptions
{
    /// <summary>Gets or sets a value indicating whether background (auto-play) keysounds are included.</summary>
    public bool IncludeBgm { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether a keysound that is triggered again while still playing
    /// is cut off, as BMS players and the game do, instead of stacking on itself.
    /// </summary>
    public bool CutRetriggeredSamples { get; set; } = true;

    /// <summary>Gets or sets how long sounds may ring past the chart's end event, in seconds.</summary>
    public double TailSeconds { get; set; } = 10;

    /// <summary>Gets or sets a value indicating whether the mix is scaled down when it would clip.</summary>
    public bool PreventClipping { get; set; } = true;

    public float Gain { get; set; } = 1f;
}

public sealed record RenderResult(float[] Samples, int TriggerCount, int MissingSamples)
{
    public double DurationSeconds => Samples.Length / 2.0 / WavDecoder.SampleRate;
}

/// <summary>
/// Renders a chart to audio by playing every note as if hit perfectly (auto-play): each lane plays
/// the keysound currently assigned to it, and background events play their keysound directly.
/// </summary>
public static class ChartRenderer
{
    public static RenderResult Render(ChartData chart, IReadOnlyList<AudioClip> clips, RenderOptions options)
    {
        var triggers = BuildTriggers(chart, options);
        int missing = 0;
        long rate = WavDecoder.SampleRate;

        // Resolve start frame and length of every voice, applying retrigger cuts.
        var voices = new List<(long Start, int Clip, long Length)>(triggers.Count);
        var lastVoiceOfClip = new Dictionary<int, int>();
        foreach (var (time, sound) in triggers)
        {
            int clip = sound - 1;
            if (clip < 0 || clip >= clips.Count || clips[clip].Frames == 0)
            {
                missing++;
                continue;
            }

            long start = Math.Max(0, time) * rate / 1000;
            if (options.CutRetriggeredSamples && lastVoiceOfClip.TryGetValue(clip, out int prev))
            {
                var p = voices[prev];
                if (p.Start + p.Length > start)
                {
                    voices[prev] = p with { Length = Math.Max(0, start - p.Start) };
                }
            }

            lastVoiceOfClip[clip] = voices.Count;
            voices.Add((start, clip, clips[clip].Frames));
        }

        long endFrame = chart.EndTime * rate / 1000;
        long capFrame = endFrame + (long)(options.TailSeconds * rate);
        long totalFrames = 0;
        foreach (var v in voices)
        {
            totalFrames = Math.Max(totalFrames, v.Start + v.Length);
        }

        totalFrames = Math.Min(totalFrames, Math.Max(capFrame, endFrame));
        var mix = new float[totalFrames * 2];
        foreach (var v in voices)
        {
            var src = clips[v.Clip].Samples;
            long frames = Math.Min(v.Length, totalFrames - v.Start);
            if (frames <= 0)
            {
                continue;
            }

            int dst = checked((int)(v.Start * 2));
            int n = checked((int)(frames * 2));

            // Short fade-out on cut voices avoids clicks.
            int fade = v.Length < clips[v.Clip].Frames ? Math.Min(n, 2 * 64) : 0;
            for (int i = 0; i < n - fade; i++)
            {
                mix[dst + i] += src[i];
            }

            for (int i = n - fade; i < n; i++)
            {
                mix[dst + i] += src[i] * ((n - i) / (float)fade);
            }
        }

        float peak = 0;
        for (int i = 0; i < mix.Length; i++)
        {
            mix[i] *= options.Gain;
            peak = Math.Max(peak, Math.Abs(mix[i]));
        }

        if (options.PreventClipping && peak > 0.98f)
        {
            float scale = 0.98f / peak;
            for (int i = 0; i < mix.Length; i++)
            {
                mix[i] *= scale;
            }
        }

        return new RenderResult(mix, voices.Count, missing);
    }

    /// <summary>Returns (time in ms, 1-based keysound) pairs for every sound the chart plays.</summary>
    internal static List<(int Time, int Sound)> BuildTriggers(ChartData chart, RenderOptions options)
    {
        // Sample assignments take effect for notes at the same timestamp, so order them first.
        var ordered = chart.Events
            .Select((e, i) => (e, i))
            .OrderBy(x => x.e.Time)
            .ThenBy(x => x.e.Type is ChartEventType.SampleP1 or ChartEventType.SampleP2 ? 0 : 1)
            .ThenBy(x => x.i)
            .Select(x => x.e);

        var lanes = new int[2, 8];
        var triggers = new List<(int, int)>();
        foreach (var e in ordered)
        {
            switch (e.Type)
            {
                case ChartEventType.SampleP1 or ChartEventType.SampleP2 when e.Param < 8:
                    lanes[e.Type - ChartEventType.SampleP1, e.Param] = e.Value;
                    break;
                case ChartEventType.NoteP1 or ChartEventType.NoteP2 when e.Param < 8:
                    int sound = lanes[e.Type - ChartEventType.NoteP1, e.Param];
                    if (sound > 0)
                    {
                        triggers.Add((e.Time, sound));

                        // Backspin scratches play the lane's sound again on release.
                        if (e.Param == 7 && e.Value != 0)
                        {
                            triggers.Add((e.Time + e.Value, sound));
                        }
                    }

                    break;
                case ChartEventType.Bgm when options.IncludeBgm && e.Value > 0:
                    triggers.Add((e.Time, e.Value));
                    break;
            }
        }

        triggers.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return triggers;
    }

    /// <summary>Writes interleaved stereo float samples as a 16-bit PCM WAV file.</summary>
    public static void WriteWav(string path, float[] samples)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        long dataBytes = (long)samples.Length * 2;
        Span<byte> h = stackalloc byte[44];
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], (uint)Math.Min(uint.MaxValue, dataBytes + 36));
        "WAVE"u8.CopyTo(h[8..]);
        "fmt "u8.CopyTo(h[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(h[20..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(h[22..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(h[24..], (uint)WavDecoder.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(h[28..], (uint)(WavDecoder.SampleRate * 4));
        BinaryPrimitives.WriteUInt16LittleEndian(h[32..], 4);
        BinaryPrimitives.WriteUInt16LittleEndian(h[34..], 16);
        "data"u8.CopyTo(h[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[40..], (uint)Math.Min(uint.MaxValue, dataBytes));
        fs.Write(h);
        var buffer = new byte[1 << 16];
        for (int i = 0; i < samples.Length;)
        {
            int n = Math.Min(buffer.Length / 2, samples.Length - i);
            for (int j = 0; j < n; j++)
            {
                short v = (short)Math.Clamp(MathF.Round(samples[i + j] * 32767f), short.MinValue, short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(j * 2), v);
            }

            fs.Write(buffer, 0, n * 2);
            i += n;
        }
    }
}
