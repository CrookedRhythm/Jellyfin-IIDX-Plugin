using System.Globalization;
using System.Text;
using System.Xml;
using IIDX.Core.Formats;

namespace IIDX.Core.Library;

/// <summary>Song facts collected for the library entry, from the music db plus the rendered chart.</summary>
public sealed record SongInfo(SongEntry Song, double BpmMin, double BpmMax, Difficulty RenderedChart, int NoteCount, bool HasMovie);

/// <summary>
/// Writes Kodi-style <c>musicvideo</c> NFO files, which Jellyfin reads into title, artist, album,
/// genre, studio, year, tags and rating. Those fields drive Jellyfin's own sort, filter and search.
/// </summary>
public static class NfoWriter
{
    private static readonly (Difficulty D, string Short, string Long)[] Charts =
    [
        (Difficulty.SpBeginner, "SPB", "SP BEGINNER"),
        (Difficulty.SpNormal, "SPN", "SP NORMAL"),
        (Difficulty.SpHyper, "SPH", "SP HYPER"),
        (Difficulty.SpAnother, "SPA", "SP ANOTHER"),
        (Difficulty.SpLeggendaria, "SPL", "SP LEGGENDARIA"),
        (Difficulty.DpBeginner, "DPB", "DP BEGINNER"),
        (Difficulty.DpNormal, "DPN", "DP NORMAL"),
        (Difficulty.DpHyper, "DPH", "DP HYPER"),
        (Difficulty.DpAnother, "DPA", "DP ANOTHER"),
        (Difficulty.DpLeggendaria, "DPL", "DP LEGGENDARIA"),
    ];

    public static string ShortName(Difficulty d) => Charts.First(c => c.D == d).Short;

    public static string LongName(Difficulty d) => Charts.First(c => c.D == d).Long;

    public static string FormatBpm(double min, double max)
    {
        static string F(double v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);
        if (max <= 0)
        {
            return "?";
        }

        return Math.Abs(max - min) < 0.01 ? F(max) : $"{F(min)}-{F(max)}";
    }

    /// <summary>Gets the tags used for filtering: per play style level, chart types, BPM range, version.</summary>
    public static List<string> Tags(SongInfo info)
    {
        var s = info.Song;
        var tags = new List<string>();
        foreach (var style in new[] { "SP", "DP" })
        {
            var levels = Charts.Where(c => c.Short.StartsWith(style, StringComparison.Ordinal) && s.Level(c.D) > 0).Select(c => s.Level(c.D)).Distinct().Order();
            tags.AddRange(levels.Select(l => $"{style} ☆{l:D2}"));
        }

        if (s.Level(Difficulty.SpLeggendaria) > 0 || s.Level(Difficulty.DpLeggendaria) > 0)
        {
            tags.Add("LEGGENDARIA");
        }

        if (s.Level(Difficulty.SpBeginner) > 0)
        {
            tags.Add("BEGINNER");
        }

        if (info.BpmMax > 0)
        {
            int bucket = (int)(Math.Floor(info.BpmMax / 10) * 10);
            tags.Add($"BPM {bucket:D3}-{bucket + 9:D3}");
            if (info.BpmMax - info.BpmMin >= 1)
            {
                tags.Add("BPM change");
            }
        }

        if (!info.HasMovie)
        {
            tags.Add("No movie");
        }

        return tags;
    }

    public static string Build(SongInfo info, string ratingChart)
    {
        var s = info.Song;
        var version = GameVersions.DisplayName(s.GameVersion);
        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, OmitXmlDeclaration = false, Encoding = Encoding.UTF8 }))
        {
            w.WriteStartDocument(true);
            w.WriteStartElement("musicvideo");
            w.WriteElementString("title", string.IsNullOrWhiteSpace(s.Title) ? s.TitleAscii : s.Title);
            if (!string.IsNullOrWhiteSpace(s.TitleAscii))
            {
                w.WriteElementString("originaltitle", s.TitleAscii);
                w.WriteElementString("sorttitle", s.TitleAscii);
            }

            if (!string.IsNullOrWhiteSpace(s.Artist))
            {
                w.WriteElementString("artist", s.Artist);
            }

            w.WriteElementString("album", version);
            if (!string.IsNullOrWhiteSpace(s.Genre))
            {
                w.WriteElementString("genre", s.Genre);
            }

            w.WriteElementString("studio", version);
            if (GameVersions.Year(s.GameVersion) is int year)
            {
                w.WriteElementString("year", year.ToString(CultureInfo.InvariantCulture));
            }

            int rating = RatingLevel(s, ratingChart);
            if (rating > 0)
            {
                w.WriteElementString("rating", rating.ToString(CultureInfo.InvariantCulture));
            }

            w.WriteElementString("plot", Plot(info, version));
            foreach (var tag in Tags(info))
            {
                w.WriteElementString("tag", tag);
            }

            w.WriteStartElement("uniqueid");
            w.WriteAttributeString("type", "iidx");
            w.WriteAttributeString("default", "true");
            w.WriteString(s.SongId.ToString(CultureInfo.InvariantCulture));
            w.WriteEndElement();
            w.WriteEndElement();
        }

        // StringBuilder output always claims utf-16; the file is written as UTF-8.
        return sb.ToString().Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"", StringComparison.Ordinal);
    }

    /// <summary>Gets the level used for the community rating, so Jellyfin can sort by difficulty.</summary>
    public static int RatingLevel(SongEntry s, string ratingChart)
    {
        static int Max(SongEntry s, string style) => Charts.Where(c => c.Short.StartsWith(style, StringComparison.Ordinal)).Max(c => s.Level(c.D));
        return ratingChart.ToUpperInvariant() switch
        {
            "NONE" => 0,
            "MAXSP" => Max(s, "SP"),
            "MAXDP" => Max(s, "DP"),
            var shortName when Charts.Any(c => c.Short == shortName) => s.Level(Charts.First(c => c.Short == shortName).D),
            _ => s.Level(Difficulty.SpAnother),
        };
    }

    private static string Plot(SongInfo info, string version)
    {
        var s = info.Song;
        var lines = new List<string>
        {
            $"{s.Genre} / {s.Artist}",
            $"BPM {FormatBpm(info.BpmMin, info.BpmMax)} · {version}",
        };
        foreach (var style in new[] { "SP", "DP" })
        {
            var parts = Charts.Where(c => c.Short.StartsWith(style, StringComparison.Ordinal) && s.Level(c.D) > 0)
                .Select(c => $"{c.Short[2]}{s.Level(c.D)}");
            var joined = string.Join(" ", parts);
            if (joined.Length > 0)
            {
                lines.Add($"{style}: {joined}");
            }
        }

        // From IIDX 32 the "subtitle" field holds licensing credits rather than part of the title.
        if (!string.IsNullOrWhiteSpace(s.Subtitle) && s.Subtitle != "0")
        {
            lines.Add(s.Subtitle);
        }

        lines.Add($"Audio rendered from the {LongName(info.RenderedChart)} chart ({info.NoteCount} notes). Song ID {s.SongId}.");
        return string.Join("\n", lines);
    }
}
