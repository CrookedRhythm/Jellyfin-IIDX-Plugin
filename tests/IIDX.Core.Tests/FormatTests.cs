using System.Buffers.Binary;
using IIDX.Core.Audio;
using IIDX.Core.Formats;

namespace IIDX.Core.Tests;

public class FormatTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Theory]
    [InlineData("music_data_v25.bin", 25)]
    [InlineData("music_data_v27.bin", 27)]
    [InlineData("music_data_v33.bin", 33)]
    public void MusicDatabase_ReadsEveryLayout(string file, int version)
    {
        var db = MusicDatabase.Load(Fixture(file));

        Assert.Equal(version, db.DataVersion);
        Assert.Equal(db.KnownEntrySize, db.EntrySize);
        Assert.Equal(6, db.Songs.Count);
        var first = db.Songs[0];
        Assert.Equal(1000, first.SongId);
        Assert.Equal("Test Song S3P", first.TitleAscii);
        Assert.Equal("TEST TECHNO", first.Genre);
        Assert.Equal("01000", first.BgaFilename);
        Assert.Equal(-120, first.BgaDelay);
        Assert.Equal(10, first.Level(Difficulty.SpAnother));
        Assert.Equal(6, first.Level(Difficulty.DpHyper));
        if (version >= 32)
        {
            Assert.Equal("テスト曲", first.Title); // UTF-16 strings from IIDX 32 on
        }

        var adpcm = db.Songs.Single(s => s.SongId == 1001);
        Assert.Equal(17, adpcm.GameVersion);
        Assert.Equal(version >= 27 ? 12 : 0, adpcm.Level(Difficulty.SpLeggendaria)); // no LEGGENDARIA column before 27
        Assert.True(db.Songs.Single(s => s.SongId == 1005).IsPlaceholder);
    }

    [Fact]
    public void MusicDatabase_ToleratesLargerEntriesFromNewerVersions()
    {
        // Simulate a future version that appends 32 bytes to every entry.
        var data = File.ReadAllBytes(Fixture("music_data_v33.bin"));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8));
        int start = 16 + (checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12))) * 4);
        int stride = (data.Length - start) / count;
        using var ms = new MemoryStream();
        ms.Write(data, 0, start);
        for (int i = 0; i < count; i++)
        {
            ms.Write(data, start + (i * stride), stride);
            ms.Write(new byte[32]);
        }

        var bytes = ms.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 34);
        var db = MusicDatabase.Parse(bytes);

        Assert.Equal(34, db.DataVersion);
        Assert.Equal(stride + 32, db.EntrySize);
        Assert.Equal(stride, db.KnownEntrySize);
        Assert.Equal(["Test Song S3P", "ADPCM Song", "Loose PCM"], db.Songs.Take(3).Select(s => s.TitleAscii));
    }

    [Fact]
    public void Ifs_ListsAndReadsFilesPackedByIfstools()
    {
        using var ifs = IfsArchive.Open(Fixture("01000.ifs"));

        Assert.Equal(["01000/01000.1", "01000/01000.s3p", "01000/01000_pre.2dx"], ifs.Files.Order(StringComparer.Ordinal));
        Assert.Equal(File.ReadAllBytes(Fixture("01002.1")), ifs.Read("01000/01000.1"));
        Assert.True(ifs.TryGetRange("01000/01000.s3p", out long offset, out int size));
        Assert.Equal(85264, size);
        Assert.True(offset > 0);
    }

    [Theory]
    [InlineData("__A9", "_A9")]
    [InlineData("_9_A9", "9 9")]
    [InlineData("____9", "__9")]
    [InlineData("_01000_E1", "01000.1")]
    [InlineData("_info_", "_info_")]
    public void Ifs_UnmanglesNames(string packed, string name) => Assert.Equal(name, IfsArchive.FixName(packed));

    [Fact]
    public void Keysounds_FromS3pInsideIfs()
    {
        using var ifs = IfsArchive.Open(Fixture("01000.ifs"));
        var archive = KeysoundArchive.Parse(ifs.Read("01000/01000.s3p"));

        Assert.Equal(9, archive.Sounds.Count);
        Assert.All(archive.Sounds, s => Assert.False(s.IsRiff)); // ASF/WMA payloads
        Assert.Equal(new byte[] { 0x30, 0x26, 0xB2, 0x75 }, archive.Sounds[0].Data[..4]); // ASF header GUID
    }

    [Fact]
    public void Chart_ReadsSlotsTempoAndNotes()
    {
        var chart = IidxChart.Parse(File.ReadAllBytes(Fixture("01002.1")));

        Assert.Equal([Difficulty.SpNormal, Difficulty.SpHyper, Difficulty.SpAnother], chart.Charts.Keys.Order());
        var spa = chart.Pick([Difficulty.SpLeggendaria, Difficulty.SpAnother], out var chosen)!;
        Assert.Equal(Difficulty.SpAnother, chosen);
        Assert.Equal(150, spa.BpmMin);
        Assert.Equal(150, spa.BpmMax);
        Assert.Equal(6000, spa.EndTime);
        Assert.True(spa.NoteCount > 0);
    }

    [Fact]
    public void MsAdpcm_MatchesFfmpegExactly()
    {
        var clip = WavDecoder.TryDecode(File.ReadAllBytes(Fixture("adpcm_ms.wav")))!;
        var reference = File.ReadAllBytes(Fixture("adpcm_ms.ffmpeg.s16le"));
        int frames = reference.Length / 4;

        Assert.Equal(frames, clip.Frames);
        for (int i = 0; i < frames * 2; i++)
        {
            short expected = BinaryPrimitives.ReadInt16LittleEndian(reference.AsSpan(i * 2));
            Assert.Equal(expected, (short)Math.Round(clip.Samples[i] * 32768f));
        }
    }

    [Fact]
    public void LocatorUsesNewestMusicDataOfInfo0AndInfo1()
    {
        var game = Directory.CreateTempSubdirectory("iidx-game-");
        try
        {
            var current = File.ReadAllBytes(Fixture("music_data_v33.bin"));
            var previous = (byte[])current.Clone();
            BinaryPrimitives.WriteUInt16LittleEndian(previous.AsSpan(8), (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(current.AsSpan(8)) - 1));

            string Write(string folder, byte[] data)
            {
                var dir = Directory.CreateDirectory(Path.Combine(game.FullName, "data", "info", folder));
                var path = Path.Combine(dir.FullName, "music_data.bin");
                File.WriteAllBytes(path, data);
                return path;
            }

            // The previous file sits in 0 and is even the more recently written one; the song count wins.
            var newer = Write("1", current);
            var older = Write("0", previous);
            File.SetLastWriteTimeUtc(newer, DateTime.UtcNow.AddDays(-1));
            Assert.Equal(newer, new IIDX.Core.Library.GameDataLocator(game.FullName, null).MusicDataPath);

            // Same version and song count: the later write time wins.
            File.WriteAllBytes(older, current);
            File.SetLastWriteTimeUtc(older, DateTime.UtcNow);
            Assert.Equal(older, IIDX.Core.Library.GameDataLocator.PickNewestMusicData([newer, older]));

            // Files that are not a music database are ignored.
            File.WriteAllBytes(older, [1, 2, 3]);
            Assert.Equal(newer, IIDX.Core.Library.GameDataLocator.PickNewestMusicData([older, newer]));
        }
        finally
        {
            game.Delete(true);
        }
    }
}
