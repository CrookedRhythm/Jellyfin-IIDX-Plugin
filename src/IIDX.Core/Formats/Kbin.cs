using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Xml;

namespace IIDX.Core.Formats;

/// <summary>
/// A node of a Konami property tree, as stored in binary XML ("kbin") or plain XML.
/// </summary>
public sealed class PropNode
{
    public PropNode(string name)
    {
        Name = name;
    }

    public string Name { get; }

    /// <summary>Gets or sets the kbin type name (s32, 3s32, str, ...), or null for a plain node.</summary>
    public string? Type { get; set; }

    public string? Text { get; set; }

    public Dictionary<string, string> Attributes { get; } = new(StringComparer.Ordinal);

    public List<PropNode> Children { get; } = new();

    public PropNode? Child(string name) => Children.FirstOrDefault(c => c.Name == name);

    public override string ToString() => $"<{Name} type={Type}>{Text}";
}

/// <summary>
/// Reader for Konami binary XML. Port of the decoding half of mon/kbinxml.
/// </summary>
public static class Kbin
{
    private const byte Signature = 0xA0;
    private const byte SigCompressed = 0x42;
    private const byte SigUncompressed = 0x45;
    private const int NodeStart = 1;
    private const int Attr = 46;
    private const int NodeEnd = 190;
    private const int EndSection = 191;
    private const string SixbitChars = "0123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZ_abcdefghijklmnopqrstuvwxyz";

    // (name, element size in bytes, element count, element kind). Count -1 means length prefixed.
    private static readonly Dictionary<int, (string Name, int Size, int Count, char Kind)> Formats = BuildFormats();

    static Kbin()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static bool IsBinary(ReadOnlySpan<byte> data) =>
        data.Length >= 2 && data[0] == Signature && (data[1] == SigCompressed || data[1] == SigUncompressed);

    /// <summary>Parses either binary or text XML into a property tree and returns its root element.</summary>
    public static PropNode Parse(byte[] data)
    {
        if (IsBinary(data))
        {
            return ParseBinary(data);
        }

        int len = data.Length;
        while (len > 0 && data[len - 1] == 0)
        {
            len--;
        }

        return ParseText(data.AsSpan(0, len).ToArray());
    }

    private static PropNode ParseText(byte[] data)
    {
        var doc = new XmlDocument();
        using (var ms = new MemoryStream(data))
        {
            doc.Load(ms);
        }

        return Convert(doc.DocumentElement ?? throw new InvalidDataException("Empty XML manifest"));

        static PropNode Convert(XmlElement e)
        {
            var node = new PropNode(e.Name);
            foreach (XmlAttribute a in e.Attributes)
            {
                if (a.Name == "__type")
                {
                    node.Type = a.Value;
                }
                else
                {
                    node.Attributes[a.Name] = a.Value;
                }
            }

            var text = new StringBuilder();
            foreach (XmlNode c in e.ChildNodes)
            {
                if (c is XmlElement ce)
                {
                    node.Children.Add(Convert(ce));
                }
                else if (c is XmlText or XmlCDataSection)
                {
                    text.Append(c.Value);
                }
            }

            node.Text = text.Length > 0 ? text.ToString().Trim() : null;
            return node;
        }
    }

    private static PropNode ParseBinary(byte[] data)
    {
        if (data[0] != Signature)
        {
            throw new InvalidDataException("Not a kbin document");
        }

        bool compressed = data[1] == SigCompressed;
        byte encKey = data[2];
        if ((byte)(data[3] ^ 0xFF) != encKey)
        {
            throw new InvalidDataException("Corrupt kbin encoding byte");
        }

        Encoding encoding = encKey switch
        {
            0x20 => Encoding.ASCII,
            0x40 => Encoding.Latin1,
            0x60 => Encoding.GetEncoding(20932), // EUC-JP
            0xA0 => Encoding.UTF8,
            _ => Encoding.GetEncoding(932),
        };

        int nodeEnd = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4)) + 8);
        int nodePos = 8;

        // Three cursors into the data section: dwords, packed bytes and packed words.
        int dataPos = nodeEnd;
        dataPos += 4; // data section size
        int bytePos = nodeEnd;
        int wordPos = nodeEnd;

        var root = new PropNode("root");
        var stack = new Stack<PropNode>();
        PropNode current = root;

        while (nodePos < nodeEnd)
        {
            while (nodePos < nodeEnd && data[nodePos] == 0)
            {
                nodePos++;
            }

            if (nodePos >= nodeEnd)
            {
                break;
            }

            int rawType = data[nodePos++];
            bool isArray = (rawType & 64) != 0;
            int nodeType = rawType & ~64;

            string name = string.Empty;
            if (nodeType != NodeEnd && nodeType != EndSection)
            {
                if (compressed)
                {
                    name = ReadSixbit(data, ref nodePos);
                }
                else
                {
                    int length = (data[nodePos++] & ~64) + 1;
                    name = encoding.GetString(data, nodePos, length);
                    nodePos += length;
                }
            }

            if (nodeType == Attr)
            {
                int size = checked((int)ReadU32(data, ref dataPos));
                string value = DecodeString(encoding, data.AsSpan(dataPos, size));
                dataPos = Align4(dataPos + size);
                current.Attributes[name] = value;
                continue;
            }

            if (nodeType == NodeEnd)
            {
                if (stack.Count > 0)
                {
                    current = stack.Pop();
                }

                continue;
            }

            if (nodeType == EndSection)
            {
                break;
            }

            var child = new PropNode(name);
            current.Children.Add(child);
            stack.Push(current);
            current = child;

            if (nodeType == NodeStart)
            {
                continue;
            }

            if (!Formats.TryGetValue(nodeType, out var fmt))
            {
                throw new NotSupportedException($"Unsupported kbin node type {nodeType}");
            }

            child.Type = fmt.Name;
            int varCount = fmt.Count;
            int arrayCount = 1;
            if (varCount == -1)
            {
                varCount = checked((int)ReadU32(data, ref dataPos));
                isArray = true;
            }
            else if (isArray)
            {
                arrayCount = checked((int)ReadU32(data, ref dataPos)) / (fmt.Size * varCount);
                child.Attributes["__count"] = arrayCount.ToString(CultureInfo.InvariantCulture);
            }

            int total = arrayCount * varCount;
            int byteLen = total * fmt.Size;
            int start;
            if (isArray)
            {
                start = dataPos;
                dataPos = Align4(dataPos + byteLen);
            }
            else
            {
                // Mirror of kbinxml's data_grab_aligned: 1 and 2 byte values are packed together.
                if (bytePos % 4 == 0)
                {
                    bytePos = dataPos;
                }

                if (wordPos % 4 == 0)
                {
                    wordPos = dataPos;
                }

                if (byteLen == 1)
                {
                    start = bytePos;
                    bytePos += 1;
                }
                else if (byteLen == 2)
                {
                    start = wordPos;
                    wordPos += 2;
                }
                else
                {
                    start = dataPos;
                    dataPos = Align4(dataPos + byteLen);
                }

                int trailing = Math.Max(bytePos, wordPos);
                if (dataPos < trailing)
                {
                    dataPos = Align4(trailing);
                }
            }

            var span = data.AsSpan(start, byteLen);
            if (nodeType == 11)
            {
                child.Text = DecodeString(encoding, span).Trim('\0');
            }
            else if (nodeType == 10)
            {
                child.Text = System.Convert.ToHexString(span).ToLowerInvariant();
            }
            else
            {
                child.Text = FormatValues(span, fmt.Size, fmt.Kind, total);
            }
        }

        return root.Children.Count > 0 ? root.Children[0] : root;
    }

    private static string DecodeString(Encoding enc, ReadOnlySpan<byte> span)
    {
        if (span.Length > 0 && span[^1] == 0)
        {
            span = span[..^1];
        }

        return enc.GetString(span);
    }

    private static string FormatValues(ReadOnlySpan<byte> span, int size, char kind, int count)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }

            var s = span.Slice(i * size, size);
            switch (kind, size)
            {
                case ('s', 1): sb.Append((sbyte)s[0]); break;
                case ('u', 1): sb.Append(s[0]); break;
                case ('s', 2): sb.Append(BinaryPrimitives.ReadInt16BigEndian(s)); break;
                case ('u', 2): sb.Append(BinaryPrimitives.ReadUInt16BigEndian(s)); break;
                case ('s', 4): sb.Append(BinaryPrimitives.ReadInt32BigEndian(s)); break;
                case ('u', 4): sb.Append(BinaryPrimitives.ReadUInt32BigEndian(s)); break;
                case ('s', 8): sb.Append(BinaryPrimitives.ReadInt64BigEndian(s)); break;
                case ('u', 8): sb.Append(BinaryPrimitives.ReadUInt64BigEndian(s)); break;
                case ('f', 4): sb.Append(BinaryPrimitives.ReadSingleBigEndian(s).ToString("F6", CultureInfo.InvariantCulture)); break;
                case ('f', 8): sb.Append(BinaryPrimitives.ReadDoubleBigEndian(s).ToString("F6", CultureInfo.InvariantCulture)); break;
                default: throw new NotSupportedException($"kbin value kind {kind}{size}");
            }
        }

        return sb.ToString();
    }

    private static uint ReadU32(byte[] data, ref int pos)
    {
        uint v = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
        pos += 4;
        return v;
    }

    private static int Align4(int v) => (v + 3) & ~3;

    private static string ReadSixbit(byte[] data, ref int pos)
    {
        int length = data[pos++];
        int lengthBytes = ((length * 6) + 7) / 8;
        var chars = new char[length];
        for (int i = 0; i < length; i++)
        {
            int bit = i * 6;
            int value = 0;
            for (int b = 0; b < 6; b++)
            {
                int abs = bit + b;
                int byteVal = data[pos + (abs >> 3)];
                int bitVal = (byteVal >> (7 - (abs & 7))) & 1;
                value = (value << 1) | bitVal;
            }

            chars[i] = SixbitChars[value];
        }

        pos += lengthBytes;
        return new string(chars);
    }

    private static Dictionary<int, (string, int, int, char)> BuildFormats()
    {
        var f = new Dictionary<int, (string, int, int, char)>
        {
            [2] = ("s8", 1, 1, 's'), [3] = ("u8", 1, 1, 'u'), [4] = ("s16", 2, 1, 's'), [5] = ("u16", 2, 1, 'u'),
            [6] = ("s32", 4, 1, 's'), [7] = ("u32", 4, 1, 'u'), [8] = ("s64", 8, 1, 's'), [9] = ("u64", 8, 1, 'u'),
            [10] = ("bin", 1, -1, 'u'), [11] = ("str", 1, -1, 'u'), [12] = ("ip4", 4, 1, 'u'), [13] = ("time", 4, 1, 'u'),
            [14] = ("float", 4, 1, 'f'), [15] = ("double", 8, 1, 'f'),
            [52] = ("bool", 1, 1, 's'), [53] = ("2b", 1, 2, 's'), [54] = ("3b", 1, 3, 's'), [55] = ("4b", 1, 4, 's'),
            [56] = ("vb", 1, 16, 's'), [48] = ("vs8", 1, 16, 's'), [49] = ("vu8", 1, 16, 'u'),
            [50] = ("vs16", 2, 8, 's'), [51] = ("vu16", 2, 8, 'u'),
        };

        // 2..4 element vectors: ids 16..45 cycle through s8,u8,s16,u16,s32,u32,s64,u64,f,d.
        var bases = new (string N, int S, char K)[]
        {
            ("s8", 1, 's'), ("u8", 1, 'u'), ("s16", 2, 's'), ("u16", 2, 'u'), ("s32", 4, 's'),
            ("u32", 4, 'u'), ("s64", 8, 's'), ("u64", 8, 'u'), ("f", 4, 'f'), ("d", 8, 'f'),
        };
        for (int n = 2; n <= 4; n++)
        {
            for (int i = 0; i < bases.Length; i++)
            {
                f[16 + ((n - 2) * 10) + i] = ($"{n}{bases[i].N}", bases[i].S, n, bases[i].K);
            }
        }

        return f;
    }
}
