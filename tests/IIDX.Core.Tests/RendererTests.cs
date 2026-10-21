using IIDX.Core.Audio;
using IIDX.Core.Formats;
using IIDX.Core.Library;

namespace IIDX.Core.Tests;

public class RendererTests
{
    private static ChartEvent E(int t, ChartEventType type, byte param, ushort value) => new(t, type, param, value);

    [Fact]
    public void Triggers_UseTheSampleLoadedForTheLane()
    {
        var chart = new ChartData(
        [
            E(0, ChartEventType.SampleP1, 1, 5),
            E(100, ChartEventType.NoteP1, 1, 0),
            E(200, ChartEventType.NoteP1, 1, 0), // the load at the same time must apply first
            E(200, ChartEventType.SampleP1, 1, 6),
            E(300, ChartEventType.NoteP2, 1, 0), // P2 lane 1 has nothing loaded: silent
            E(400, ChartEventType.Bgm, 0, 9),
            E(500, ChartEventType.SampleP1, 7, 3),
            E(500, ChartEventType.NoteP1, 7, 250), // backspin scratch: sound again on release
            E(1000, ChartEventType.End, 0, 0),
        ]);

        var triggers = ChartRenderer.BuildTriggers(chart, new RenderOptions());

        Assert.Equal([(100, 5), (200, 6), (400, 9), (500, 3), (750, 3)], triggers);
        Assert.DoesNotContain(ChartRenderer.BuildTriggers(chart, new RenderOptions { IncludeBgm = false }), t => t.Sound == 9);
    }

    [Fact]
    public void Render_MixesAtTheRightTimeAndCutsRetriggers()
    {
        var one = new AudioClip(Enumerable.Repeat(0.25f, 2 * 44100).ToArray()); // 1 s of DC
        var chart = new ChartData([E(0, ChartEventType.Bgm, 0, 1), E(500, ChartEventType.Bgm, 0, 1), E(600, ChartEventType.End, 0, 0)]);

        var cut = ChartRenderer.Render(chart, [one], new RenderOptions());
        var stacked = ChartRenderer.Render(chart, [one], new RenderOptions { CutRetriggeredSamples = false });

        Assert.Equal(1.5, cut.DurationSeconds, 2);
        Assert.Equal(0.25f, cut.Samples[2 * 22050 + 2 * 100], 3); // after the retrigger only one voice plays
        Assert.Equal(0.5f, stacked.Samples[2 * 22050 + 2 * 100], 3);
        Assert.Equal(0.25f, cut.Samples[2 * 20000], 3); // the first voice fades only right at the cut
    }

    [Fact]
    public void Render_ScalesDownInsteadOfClipping()
    {
        var loud = new AudioClip(Enumerable.Repeat(0.8f, 200).ToArray());
        var chart = new ChartData([E(0, ChartEventType.Bgm, 0, 1), E(0, ChartEventType.Bgm, 0, 2), E(10, ChartEventType.End, 0, 0)]);

        var r = ChartRenderer.Render(chart, [loud, loud], new RenderOptions());

        Assert.True(r.Samples.Max() <= 0.98f + 1e-6);
    }

    [Fact]
    public void Nfo_CarriesSortAndFilterFields()
    {
        var song = new SongEntry
        {
            SongId = 25001,
            Title = "冥",
            TitleAscii = "Mei",
            Genre = "ANTHEM",
            Artist = "Amuro vs Killer",
            GameVersion = 9,
            Levels = [0, 6, 10, 12, 0, 0, 6, 10, 12, 0],
            Subtitle = "0",
        };
        var nfo = NfoWriter.Build(new SongInfo(song, 143, 143, Difficulty.SpAnother, 1600, true), "SPA");

        Assert.Contains("<title>冥</title>", nfo);
        Assert.Contains("<sorttitle>Mei</sorttitle>", nfo);
        Assert.Contains("<album>beatmania IIDX 9th style</album>", nfo);
        Assert.Contains("<year>2003</year>", nfo);
        Assert.Contains("<rating>12</rating>", nfo);
        Assert.Contains("<tag>SP ☆12</tag>", nfo);
        Assert.Contains("<tag>BPM 140-149</tag>", nfo);
        Assert.DoesNotContain("\n0\n", nfo);
    }

    [Theory]
    [InlineData("/lib/01000 Test Song/01000 Test Song.mp4", true, 1000)]
    [InlineData("/lib/33001 x/33001 x.mkv", true, 33001)]
    [InlineData("/lib/other/movie.mp4", false, 0)]
    public void SongIdFromPath(string path, bool ok, int id)
    {
        Assert.Equal(ok, IidxLibrary.TryGetSongId(path, out int got));
        Assert.Equal(id, got);
    }
}
