using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace IIDX.Core.Formats;

/// <summary>
/// Read-only access to Konami <c>.ifs</c> archives. Only plain (uncompressed) file entries are
/// supported, which covers the <c>data/sound</c> archives this plugin needs.
/// </summary>
public sealed class IfsArchive : IDisposable
{
    private const uint Signature = 0x6CAD8F89;
    private const ushort FlagHasMd5 = 2;

    private readonly FileStream _stream;
    private readonly long _dataStart;
    private readonly Dictionary<string, (long Offset, int Size)> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    private IfsArchive(FileStream stream)
    {
        _stream = stream;
        Span<byte> header = stackalloc byte[36];
        stream.ReadExactly(header[..20]);
        if (BinaryPrimitives.ReadUInt32BigEndian(header) != Signature)
        {
            throw new InvalidDataException("Not an IFS archive");
        }

        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(header[4..]);
        _dataStart = BinaryPrimitives.ReadUInt32BigEndian(header[16..]);
        int headerSize = 20;
        if ((flags & FlagHasMd5) != 0)
        {
            headerSize += 16;
        }

        var manifest = new byte[_dataStart - headerSize];
        stream.Position = headerSize;
        stream.ReadExactly(manifest);
        var root = Kbin.Parse(manifest);
        Walk(root, string.Empty);
    }

    /// <summary>Gets the paths of all files in the archive, using '/' as the separator.</summary>
    public IReadOnlyCollection<string> Files => _files.Keys;

    public static IfsArchive Open(string path) =>
        new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess));

    public bool Contains(string path) => _files.ContainsKey(path);

    /// <summary>Gets the absolute byte range of a file inside the archive, for readers that seek directly.</summary>
    public bool TryGetRange(string path, out long offset, out int size)
    {
        if (_files.TryGetValue(path, out var entry))
        {
            offset = _dataStart + entry.Offset;
            size = entry.Size;
            return true;
        }

        offset = 0;
        size = 0;
        return false;
    }

    public byte[] Read(string path)
    {
        if (!_files.TryGetValue(path, out var entry))
        {
            throw new FileNotFoundException($"'{path}' is not in the IFS archive");
        }

        var buf = new byte[entry.Size];
        lock (_lock)
        {
            _stream.Position = _dataStart + entry.Offset;
            _stream.ReadExactly(buf);
        }

        return buf;
    }

    public void Dispose() => _stream.Dispose();

    /// <summary>Reverses the name mangling ifs manifests apply to make file names valid XML tags.</summary>
    internal static string FixName(string n)
    {
        if (n is "_info_" or "_super_")
        {
            return n;
        }

        var sb = new StringBuilder(n.Length);
        for (int i = 0; i < n.Length; i++)
        {
            char c = n[i];
            if (c != '_' || i + 1 >= n.Length)
            {
                sb.Append(c);
                continue;
            }

            char key = n[++i];
            if (i == 1 && char.IsAsciiDigit(key))
            {
                sb.Append(key);
                continue;
            }

            sb.Append(key switch
            {
                'A' => ' ',
                'B' => '$',
                'C' => '+',
                'D' => '-',
                'E' => '.',
                'F' => ':',
                'G' => '@',
                'H' => '~',
                '_' => '_',
                _ => throw new InvalidDataException($"Invalid escaped IFS name '{n}'"),
            });
        }

        return sb.ToString();
    }

    private void Walk(PropNode folder, string prefix)
    {
        foreach (var child in folder.Children)
        {
            string name = FixName(child.Name);
            if (name is "_info_" or "_super_")
            {
                continue;
            }

            // Files that live in a referenced "super" archive carry an <i> child; we skip them.
            if (child.Children.Count > 0 && child.Children[0].Name == "i")
            {
                continue;
            }

            string path = prefix.Length == 0 ? name : prefix + "/" + name;
            var parts = (child.Text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (child.Children.Count > 0 || parts.Length <= 1)
            {
                Walk(child, path);
                continue;
            }

            long start = long.Parse(parts[0], CultureInfo.InvariantCulture);
            int size = int.Parse(parts[1], CultureInfo.InvariantCulture);
            _files[path] = (start, size);
        }
    }
}
