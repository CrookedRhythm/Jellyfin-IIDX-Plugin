using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace IIDX.Core.Media;

public sealed class FFmpegException : Exception
{
    public FFmpegException(string message)
        : base(message)
    {
    }
}

/// <summary>Thin wrapper for running ffmpeg/ffprobe with an argument list.</summary>
public sealed class FFmpeg
{
    public FFmpeg(string ffmpegPath, string? ffprobePath = null)
    {
        FFmpegPath = ffmpegPath;
        FFprobePath = ffprobePath ?? GuessProbe(ffmpegPath);
    }

    public string FFmpegPath { get; }

    public string FFprobePath { get; }

    public async Task RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var (code, _, err) = await ExecAsync(FFmpegPath, ["-hide_banner", "-nostdin", "-loglevel", "error", "-y", .. args], ct).ConfigureAwait(false);
        if (code != 0)
        {
            throw new FFmpegException($"ffmpeg exited with {code}: {Tail(err)}");
        }
    }

    /// <summary>Returns the duration of a media file in seconds, or null if ffprobe could not read it.</summary>
    public async Task<double?> ProbeDurationAsync(string path, CancellationToken ct)
    {
        var (code, output, _) = await ExecAsync(
            FFprobePath,
            ["-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", path],
            ct).ConfigureAwait(false);
        return code == 0 && double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    /// <summary>Returns the codec name of the first video stream, or null.</summary>
    public async Task<string?> ProbeVideoCodecAsync(string path, CancellationToken ct)
    {
        var (code, output, _) = await ExecAsync(
            FFprobePath,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_name", "-of", "default=noprint_wrappers=1:nokey=1", path],
            ct).ConfigureAwait(false);
        var codec = output.Trim();
        return code == 0 && codec.Length > 0 ? codec : null;
    }

    private static async Task<(int Code, string Out, string Err)> ExecAsync(string exe, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                stdout.AppendLine(e.Data);
            }
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                stderr.AppendLine(e.Data);
            }
        };

        if (!p.Start())
        {
            throw new FFmpegException($"Could not start {exe}");
        }

        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                p.Kill(true);
            }
            catch (InvalidOperationException)
            {
            }

            throw;
        }

        p.WaitForExit();
        return (p.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static string GuessProbe(string ffmpeg)
    {
        var dir = Path.GetDirectoryName(ffmpeg);
        var name = Path.GetFileName(ffmpeg).Replace("ffmpeg", "ffprobe", StringComparison.OrdinalIgnoreCase);
        return string.IsNullOrEmpty(dir) ? name : Path.Combine(dir, name);
    }

    private static string Tail(string s)
    {
        s = s.Trim();
        return s.Length > 1500 ? "..." + s[^1500..] : s;
    }
}
