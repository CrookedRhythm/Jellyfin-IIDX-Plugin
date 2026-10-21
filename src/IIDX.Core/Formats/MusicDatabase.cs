using System.Buffers.Binary;
using System.Text;

namespace IIDX.Core.Formats;

/// <summary>Chart difficulties in the order the music database stores them from IIDX 27 on.</summary>
public enum Difficulty
{
    SpBeginner = 0,
    SpNormal = 1,
    SpHyper = 2,
    SpAnother = 3,
    SpLeggendaria = 4,
    DpBeginner = 5,
    DpNormal = 6,
    DpHyper = 7,
    DpAnother = 8,
    DpLeggendaria = 9,
}

/// <summary>One song entry from <c>music_data.bin</c>.</summary>
public sealed record SongEntry
{
    public required int SongId { get; init; }

    public required string Title { get; init; }

    public required string TitleAscii { get; init; }

    public required string Genre { get; init; }

    public required string Artist { get; init; }

    public string Subtitle { get; init; } = string.Empty;

    /// <summary>Gets the style the song debuted in (0 = substream, 1 = 1st style, ... 33 = Sparkle Shower).</summary>
    public required int GameVersion { get; init; }

    /// <summary>Gets the chart levels, indexed by <see cref="Difficulty"/>. 0 means the chart does not exist.</summary>
    public required int[] Levels { get; init; }

    public int Volume { get; init; }

    public string BgaFilename { get; init; } = string.Empty;

    public int BgaDelay { get; init; }

    public int Level(Difficulty d) => Levels[(int)d];

    /// <summary>Gets the 5 digit id the game uses for file and folder names.</summary>
    public string FileId => SongId.ToString("D5", System.Globalization.CultureInfo.InvariantCulture);

    public bool IsPlaceholder =>
        (Artist == "event_data" && Genre == "event_data") || (string.IsNullOrWhiteSpace(Title) && string.IsNullOrWhiteSpace(TitleAscii));
}

/// <summary>
/// Parser for the IIDX music database (<c>data/info/0/music_data.bin</c>). The entry layout follows
/// drmext/iidx-datatools' musicdata_tool and supports data versions 20 (tricoro) through 33 (Sparkle Shower),
/// plus 80 (INFINITAS). Later versions are read with the newest known layout.
/// </summary>
public sealed class MusicDatabase
{
    static MusicDatabase()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private MusicDatabase(int dataVersion, IReadOnlyList<SongEntry> songs, int entrySize, int knownEntrySize)
    {
        DataVersion = dataVersion;
        Songs = songs;
        EntrySize = entrySize;
        KnownEntrySize = knownEntrySize;
    }

    /// <summary>Gets the data version from the file header, which matches the game's style number.</summary>
    public int DataVersion { get; }

    public IReadOnlyList<SongEntry> Songs { get; }

    /// <summary>Gets the size of one entry in this file.</summary>
    public int EntrySize { get; }

    /// <summary>Gets how many bytes of each entry the parser understands. Less than <see cref="EntrySize"/> means a newer layout.</summary>
    public int KnownEntrySize { get; }

    public static MusicDatabase Load(string path) => Parse(File.ReadAllBytes(path));

    public static MusicDatabase Parse(byte[] data)
    {
        if (data.Length < 16 || Encoding.ASCII.GetString(data, 0, 4) != "IIDX")
        {
            throw new InvalidDataException("Not an IIDX music_data.bin (missing IIDX signature)");
        }

        int version = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)));
        if (version is < 20 or > 99)
        {
            throw new NotSupportedException($"Unsupported music_data.bin version {version}");
        }

        bool modern = version >= 32 && version != 80;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8));
        int indexCount = modern
            ? checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12)))
            : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(10));
        int pos = 16 + (indexCount * (modern ? 4 : 2));

        // Entries are fixed size. Measuring the stride from the file, instead of trusting a table of
        // known sizes, lets newer data versions that append fields still parse with the latest layout.
        int expected = KnownLayoutSize(version, modern);
        int stride = count > 0 ? (data.Length - pos) / count : expected;
        if (stride < expected)
        {
            throw new InvalidDataException($"music_data.bin v{version}: entries are {stride} bytes, expected at least {expected}");
        }

        var songs = new List<SongEntry>(count);
        var cp932 = Encoding.GetEncoding(932);
        for (int i = 0; i < count; i++)
        {
            var r = new EntryReader(data.AsSpan(pos + (i * stride), stride), 0);
            songs.Add(ReadEntry(ref r, version, modern, cp932));
        }

        return new MusicDatabase(version, songs, stride, expected);
    }

    /// <summary>Gets the entry size the known layout consumes for a data version.</summary>
    internal static int KnownLayoutSize(int version, bool modern)
    {
        int strings = version == 80 ? 0x80 + 0x40 + 0x80 + 0x80 : modern ? 0x100 + 0x40 + 0x80 + 0x100 + 0x100 : 0x100;
        int size = strings + 20 + (modern ? 4 : 0) + 6 + (modern ? 14 : 6);
        size += version >= 27 ? 10 : 8;
        size += version == 80 ? 0x2C6 : version >= 27 ? 0x286 : 0xA0;
        size += 8 + (version >= 27 ? 10 : 8) + 2;
        size += version <= 26 || version == 80 ? 2 : 0;
        size += 0x20 + (version == 80 ? 2 : 0) + 4 + (0x20 * (version >= 22 ? 10 : 9)) + (version >= 26 ? 4 : 0);
        return size;
    }

    private static SongEntry ReadEntry(ref EntryReader r, int version, bool modern, Encoding cp932)
    {
        string title, titleAscii, genre, artist, subtitle = string.Empty;
        if (version == 80)
        {
            title = r.String(0x80, cp932);
            titleAscii = r.String(0x40, cp932);
            genre = r.String(0x80, cp932);
            artist = r.String(0x80, cp932);
        }
        else if (modern)
        {
            title = r.String(0x100, Encoding.Unicode);
            titleAscii = r.String(0x40, cp932);
            genre = r.String(0x80, Encoding.Unicode);
            artist = r.String(0x100, Encoding.Unicode);
            subtitle = r.String(0x100, Encoding.Unicode);
        }
        else
        {
            title = r.String(0x40, cp932);
            titleAscii = r.String(0x40, cp932);
            genre = r.String(0x40, cp932);
            artist = r.String(0x40, cp932);
        }

        r.Skip(20); // texture ids: title, artist, genre, load, list
        if (modern)
        {
            r.Skip(4); // subtitle texture
        }

        r.Skip(4); // font index
        int gameVersion = r.U16();
        r.Skip(modern ? 14 : 6); // folder flags

        var levels = new int[10];
        if (version >= 27)
        {
            for (int i = 0; i < 10; i++)
            {
                levels[i] = r.U8();
            }
        }
        else
        {
            levels[(int)Difficulty.SpNormal] = r.U8();
            levels[(int)Difficulty.SpHyper] = r.U8();
            levels[(int)Difficulty.SpAnother] = r.U8();
            levels[(int)Difficulty.DpNormal] = r.U8();
            levels[(int)Difficulty.DpHyper] = r.U8();
            levels[(int)Difficulty.DpAnother] = r.U8();
            levels[(int)Difficulty.SpBeginner] = r.U8();
            levels[(int)Difficulty.DpBeginner] = r.U8();
        }

        r.Skip(version == 80 ? 0x2C6 : version >= 27 ? 0x286 : 0xA0);

        int songId = checked((int)r.U32());
        int volume = checked((int)r.U32());
        r.Skip(version >= 27 ? 10 : 8); // chart identifiers
        int bgaDelay = r.S16();
        if (version <= 26 || version == 80)
        {
            r.Skip(2);
        }

        string bga = r.String(0x20, cp932);
        if (version == 80)
        {
            r.Skip(2);
        }

        r.Skip(4); // afp flag
        r.Skip(0x20 * (version >= 22 ? 10 : 9));
        if (version >= 26)
        {
            r.Skip(4);
        }

        return new SongEntry
        {
            SongId = songId,
            Title = title,
            TitleAscii = titleAscii,
            Genre = genre,
            Artist = artist,
            Subtitle = subtitle,
            GameVersion = gameVersion,
            Levels = levels,
            Volume = volume,
            BgaFilename = bga,
            BgaDelay = bgaDelay,
        };
    }

    private ref struct EntryReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _pos;

        public EntryReader(ReadOnlySpan<byte> data, int pos)
        {
            _data = data;
            _pos = pos;
        }

        public void Skip(int n) => _pos += n;

        public int U8() => _data[_pos++];

        public int U16()
        {
            int v = BinaryPrimitives.ReadUInt16LittleEndian(_data[_pos..]);
            _pos += 2;
            return v;
        }

        public int S16()
        {
            int v = BinaryPrimitives.ReadInt16LittleEndian(_data[_pos..]);
            _pos += 2;
            return v;
        }

        public uint U32()
        {
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(_data[_pos..]);
            _pos += 4;
            return v;
        }

        public string String(int length, Encoding enc)
        {
            var span = _data.Slice(_pos, length);
            _pos += length;
            int end;
            if (enc == Encoding.Unicode)
            {
                end = 0;
                while (end + 1 < span.Length && (span[end] != 0 || span[end + 1] != 0))
                {
                    end += 2;
                }
            }
            else
            {
                end = span.IndexOf((byte)0);
                if (end < 0)
                {
                    end = span.Length;
                }
            }

            return enc.GetString(span[..end]).Trim();
        }
    }
}
