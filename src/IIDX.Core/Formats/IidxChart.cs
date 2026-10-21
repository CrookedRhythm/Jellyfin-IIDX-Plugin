using System.Buffers.Binary;

namespace IIDX.Core.Formats;

/// <summary>Event types found in <c>.1</c> chart files.</summary>
public enum ChartEventType : byte
{
    NoteP1 = 0,
    NoteP2 = 1,
    SampleP1 = 2,
    SampleP2 = 3,
    Tempo = 4,
    Meter = 5,
    End = 6,
    Bgm = 7,
    Timing = 8,
    Measure = 12,
    NoteCount = 16,
}

/// <summary>One 8 byte chart event. <see cref="Time"/> is in milliseconds.</summary>
public readonly record struct ChartEvent(int Time, ChartEventType Type, byte Param, ushort Value);

/// <summary>A single difficulty from a <c>.1</c> file.</summary>
public sealed class ChartData
{
    public ChartData(IReadOnlyList<ChartEvent> events)
    {
        Events = events;
        double? min = null, max = null;
        foreach (var e in events)
        {
            switch (e.Type)
            {
                case ChartEventType.NoteP1 or ChartEventType.NoteP2:
                    NoteCount += e.Value != 0 ? 2 : 1;
                    break;
                case ChartEventType.Tempo when e.Param != 0:
                    double bpm = (double)e.Value / e.Param;
                    min = min is null ? bpm : Math.Min(min.Value, bpm);
                    max = max is null ? bpm : Math.Max(max.Value, bpm);
                    break;
                case ChartEventType.End:
                    EndTime = Math.Max(EndTime, e.Time);
                    break;
            }

            LastEventTime = Math.Max(LastEventTime, e.Time);
        }

        BpmMin = min ?? 0;
        BpmMax = max ?? 0;
        if (EndTime == 0)
        {
            EndTime = LastEventTime;
        }
    }

    public IReadOnlyList<ChartEvent> Events { get; }

    public int NoteCount { get; }

    public double BpmMin { get; }

    public double BpmMax { get; }

    public int EndTime { get; }

    public int LastEventTime { get; }
}

/// <summary>
/// Reader for IIDX <c>.1</c> chart containers: a 12 slot header of (offset, length) pairs followed by
/// the event lists. Slot layout per iidx2bms/one2bme: 0 SPH, 1 SPN, 2 SPA, 3 SPB, 4 SPL, 6 DPH, 7 DPN, 8 DPA, 9 DPB, 10 DPL.
/// </summary>
public sealed class IidxChart
{
    private static readonly Dictionary<Difficulty, int> SlotOf = new()
    {
        [Difficulty.SpHyper] = 0,
        [Difficulty.SpNormal] = 1,
        [Difficulty.SpAnother] = 2,
        [Difficulty.SpBeginner] = 3,
        [Difficulty.SpLeggendaria] = 4,
        [Difficulty.DpHyper] = 6,
        [Difficulty.DpNormal] = 7,
        [Difficulty.DpAnother] = 8,
        [Difficulty.DpBeginner] = 9,
        [Difficulty.DpLeggendaria] = 10,
    };

    private readonly Dictionary<Difficulty, ChartData> _charts;

    private IidxChart(Dictionary<Difficulty, ChartData> charts)
    {
        _charts = charts;
    }

    public IReadOnlyDictionary<Difficulty, ChartData> Charts => _charts;

    public static int Slot(Difficulty d) => SlotOf[d];

    public static IidxChart Parse(byte[] data)
    {
        if (data.Length < 96)
        {
            throw new InvalidDataException("Chart file is too short");
        }

        var charts = new Dictionary<Difficulty, ChartData>();
        foreach (var (diff, slot) in SlotOf)
        {
            int offset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(slot * 8)));
            int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((slot * 8) + 4)));
            if (length == 0 || offset <= 0 || offset >= data.Length)
            {
                continue;
            }

            int end = Math.Min(data.Length, offset + length);
            var events = new List<ChartEvent>(length / 8);
            for (int p = offset; p + 8 <= end; p += 8)
            {
                int time = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(p));
                if (time == 0x7FFFFFFF)
                {
                    break;
                }

                events.Add(new ChartEvent(
                    time,
                    (ChartEventType)data[p + 4],
                    data[p + 5],
                    BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p + 6))));
            }

            if (events.Count > 0)
            {
                charts[diff] = new ChartData(events);
            }
        }

        return new IidxChart(charts);
    }

    /// <summary>Picks the first chart in <paramref name="preference"/> that exists in this file.</summary>
    public ChartData? Pick(IEnumerable<Difficulty> preference, out Difficulty chosen)
    {
        foreach (var d in preference)
        {
            if (_charts.TryGetValue(d, out var c))
            {
                chosen = d;
                return c;
            }
        }

        chosen = default;
        return null;
    }
}
