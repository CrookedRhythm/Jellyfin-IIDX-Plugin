using System.Buffers.Binary;

namespace IIDX.Core.Formats;

/// <summary>An encoded keysound: a RIFF/WAVE file from a .2dx or an ASF/WMA file from a .s3p.</summary>
public sealed record Keysound(int Index, byte[] Data, int Attenuation)
{
    public bool IsRiff => Data.Length >= 12 && Data.AsSpan(0, 4).SequenceEqual("RIFF"u8) && Data.AsSpan(8, 4).SequenceEqual("WAVE"u8);
}

/// <summary>
/// Reads keysound containers. Chart events reference keysounds by 1-based index, so
/// <see cref="Sounds"/>[i] is keysound number i + 1.
/// </summary>
public sealed class KeysoundArchive
{
    private KeysoundArchive(IReadOnlyList<Keysound> sounds)
    {
        Sounds = sounds;
    }

    public IReadOnlyList<Keysound> Sounds { get; }

    public static KeysoundArchive Parse(byte[] data)
    {
        if (data.Length >= 8 && data.AsSpan(0, 4).SequenceEqual("S3P0"u8))
        {
            return ParseS3p(data);
        }

        return Parse2dx(data);
    }

    private static KeysoundArchive ParseS3p(byte[] data)
    {
        int count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)));
        var list = new List<Keysound>(count);
        for (int i = 0; i < count; i++)
        {
            int entry = 8 + (i * 8);
            int offset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(entry)));
            int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(entry + 4)));
            if (offset + 12 > data.Length || !data.AsSpan(offset, 4).SequenceEqual("S3V0"u8))
            {
                throw new InvalidDataException($"s3p entry {i + 1} is not an S3V0 block");
            }

            int dataOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4)));
            int dataSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 8)));
            if (dataSize <= 0 || offset + dataOffset + dataSize > data.Length)
            {
                dataSize = length - dataOffset;
            }

            list.Add(new Keysound(i + 1, data.AsSpan(offset + dataOffset, dataSize).ToArray(), 0));
        }

        return new KeysoundArchive(list);
    }

    private static KeysoundArchive Parse2dx(byte[] data)
    {
        if (data.Length < 72)
        {
            throw new InvalidDataException("2dx file is too short");
        }

        int headerSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16)));
        int count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(20)));
        if (count < 0 || 72 + (count * 4) > data.Length || (headerSize != 0 && headerSize < 72 + (count * 4)))
        {
            throw new InvalidDataException("Unrecognised 2dx header");
        }

        var list = new List<Keysound>(count);
        for (int i = 0; i < count; i++)
        {
            int offset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(72 + (i * 4))));
            if (offset + 24 > data.Length || !data.AsSpan(offset, 4).SequenceEqual("2DX9"u8))
            {
                throw new InvalidDataException($"2dx entry {i + 1} is missing its 2DX9 header");
            }

            int entryHeader = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4)));
            int wavSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 8)));
            int attenuation = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset + 18));
            int start = offset + entryHeader;
            wavSize = Math.Min(wavSize, data.Length - start);
            list.Add(new Keysound(i + 1, data.AsSpan(start, wavSize).ToArray(), attenuation));
        }

        return new KeysoundArchive(list);
    }
}
