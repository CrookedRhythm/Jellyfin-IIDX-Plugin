using IIDX.Core.Formats;
using IIDX.Core.Media;

namespace IIDX.Core.Audio;

/// <summary>
/// Decodes every keysound of an archive to PCM. WAV keysounds are decoded in process; anything
/// else (the WMA payloads of .s3p archives) goes through ffmpeg, many files per invocation.
/// </summary>
public static class KeysoundDecoder
{
    private const int BatchSize = 48;

    public static async Task<AudioClip[]> DecodeAllAsync(KeysoundArchive archive, FFmpeg? ffmpeg, string tempDir, CancellationToken ct)
    {
        var clips = new AudioClip[archive.Sounds.Count];
        var pending = new List<int>();
        for (int i = 0; i < archive.Sounds.Count; i++)
        {
            var ks = archive.Sounds[i];
            var clip = ks.Data.Length == 0 ? AudioClip.Empty : ks.IsRiff ? WavDecoder.TryDecode(ks.Data) : null;
            if (clip != null)
            {
                clips[i] = clip;
            }
            else
            {
                pending.Add(i);
            }
        }

        if (pending.Count == 0)
        {
            return clips;
        }

        if (ffmpeg == null)
        {
            throw new InvalidOperationException($"{pending.Count} keysounds need ffmpeg to decode but no ffmpeg was configured");
        }

        Directory.CreateDirectory(tempDir);
        try
        {
            // Batches run side by side; on a 4 core box this cuts decode time for ~800 WMA keysounds
            // from ~7 s to ~2 s.
            var batches = pending.Chunk(BatchSize).ToList();
            await Parallel.ForEachAsync(
                batches,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 8), CancellationToken = ct },
                async (batch, token) => await DecodeBatchAsync(archive, batch, clips, ffmpeg, tempDir, token).ConfigureAwait(false)).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, true);
            }
            catch (IOException)
            {
            }
        }

        return clips;
    }

    private static async Task DecodeBatchAsync(KeysoundArchive archive, int[] batch, AudioClip[] clips, FFmpeg ffmpeg, string tempDir, CancellationToken ct)
    {
        string rate = WavDecoder.SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var args = new List<string>();
        foreach (int idx in batch)
        {
            var input = Path.Combine(tempDir, $"{idx + 1:D4}.in");
            await File.WriteAllBytesAsync(input, archive.Sounds[idx].Data, ct).ConfigureAwait(false);
            args.Add("-i");
            args.Add(input);
        }

        for (int j = 0; j < batch.Length; j++)
        {
            args.AddRange(["-map", $"{j}:a:0", "-f", "f32le", "-acodec", "pcm_f32le", "-ar", rate, "-ac", "2", Path.Combine(tempDir, $"{batch[j] + 1:D4}.raw")]);
        }

        try
        {
            await ffmpeg.RunAsync(args, ct).ConfigureAwait(false);
        }
        catch (FFmpegException)
        {
            // One bad keysound fails the whole batch; retry one at a time so the others survive.
            foreach (int idx in batch)
            {
                var input = Path.Combine(tempDir, $"{idx + 1:D4}.in");
                var output = Path.Combine(tempDir, $"{idx + 1:D4}.raw");
                try
                {
                    await ffmpeg.RunAsync(["-i", input, "-f", "f32le", "-acodec", "pcm_f32le", "-ar", rate, "-ac", "2", output], ct).ConfigureAwait(false);
                }
                catch (FFmpegException)
                {
                    File.Delete(output);
                }
            }
        }

        foreach (int idx in batch)
        {
            var raw = Path.Combine(tempDir, $"{idx + 1:D4}.raw");
            clips[idx] = File.Exists(raw) ? new AudioClip(ReadFloats(await File.ReadAllBytesAsync(raw, ct).ConfigureAwait(false))) : AudioClip.Empty;
        }
    }

    private static float[] ReadFloats(byte[] bytes)
    {
        var floats = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, floats, 0, floats.Length * 4);
        return floats;
    }
}
