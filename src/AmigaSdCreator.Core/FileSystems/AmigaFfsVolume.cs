using System.Text;
using AmigaSdCreator.Core.IO;
using AmigaSdCreator.Core.Partitioning;

namespace AmigaSdCreator.Core.FileSystems;

public sealed record AmigaEntry
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public DateTime Modified { get; init; }
    /// <summary>AmigaDOS protection bits as stored (HSPARWED; RWED are active-low).</summary>
    public uint Protection { get; init; }
    public string Comment { get; init; } = "";
    internal uint HeaderBlock { get; init; }

    /// <summary>Protection in the usual "hsparwed" notation, '-' for clear flags.</summary>
    public string ProtectionText
    {
        get
        {
            const string letters = "hsparwed";
            var c = new char[8];
            for (int i = 0; i < 8; i++)
            {
                int bit = 7 - i;
                bool set = (Protection >> bit & 1) != 0;
                // r, w, e, d (bits 3-0) are "denied" when set.
                bool shown = bit >= 4 ? set : !set;
                c[i] = shown ? letters[i] : '-';
            }
            return new string(c);
        }
    }
}

public sealed class AmigaFsException(string message) : Exception(message);

/// <summary>
/// Read/write access to an OFS/FFS volume (DOS\0 - DOS\3). DirCache volumes (DOS\4, DOS\5) are
/// opened read-only because writing would require maintaining the directory cache blocks.
/// Long-name variants (DOS\6, DOS\7) are not supported.
///
/// Write safety follows AmigaDOS: the root's bitmap-valid flag is cleared before the first change
/// and only set again once the bitmap has been written by <see cref="Flush"/>. If the process is
/// interrupted, the Amiga sees an invalid bitmap and revalidates the volume rather than trusting it.
/// </summary>
public sealed class AmigaFfsVolume : IDisposable
{
    private const int BS = 512;
    private const int HashSize = 72;
    private const int TypeHeader = 2, TypeData = 8, TypeList = 16;
    private const int StRoot = 1, StUserDir = 2, StFile = -3;
    private const int OfsDataBytes = BS - 24;

    private readonly IBlockDevice _dev;
    private readonly long _blocks;
    private readonly uint _root;
    private readonly List<uint> _bitmapBlocks = [];
    private readonly byte[][] _bitmap;
    private bool _dirty;
    private uint _allocCursor;

    public DosType DosType { get; }
    public string VolumeName { get; private set; }
    public bool IsReadOnly { get; }
    public bool IsFastFileSystem => DosType.IsFastFileSystem;
    public long TotalBytes => _blocks * BS;
    public long FreeBytes => CountFree() * BS;
    private bool Intl => DosType.IsInternational;

    private AmigaFfsVolume(IBlockDevice dev, DosType dosType, bool readOnly)
    {
        _dev = dev;
        _blocks = dev.SectorCount();
        DosType = dosType;
        IsReadOnly = readOnly;
        _root = (uint)AmigaFfsFormatter.RootBlockFor(_blocks);
        var r = Read(_root);
        if (Endian.U32BE(r, 0) != TypeHeader || Endian.I32BE(r, BS - 4) != StRoot || !AmigaChecksum.IsSumZero(r, 128))
            throw new AmigaFsException("The root block is missing or damaged. The volume cannot be opened safely.");
        VolumeName = Endian.BcplString(r, BS - 80, 30);
        if (!readOnly && Endian.U32BE(r, BS - 200) != 0xFFFFFFFF)
            throw new AmigaFsException("The volume's bitmap is marked invalid (it was not cleanly closed). Let the Amiga validate it before writing from Windows.");

        for (int i = 0; i < 25; i++)
        {
            uint p = Endian.U32BE(r, BS - 196 + i * 4);
            if (p != 0) _bitmapBlocks.Add(p);
        }
        var seen = new HashSet<uint>();
        for (uint ext = Endian.U32BE(r, BS - 96); ext != 0 && ext < _blocks && seen.Add(ext);)
        {
            var e = Read(ext);
            for (int i = 0; i < 127; i++)
            {
                uint p = Endian.U32BE(e, i * 4);
                if (p != 0) _bitmapBlocks.Add(p);
            }
            ext = Endian.U32BE(e, BS - 4);
        }
        long needed = (_blocks - 2 + AmigaFfsFormatter.BitsPerBitmapBlock - 1) / AmigaFfsFormatter.BitsPerBitmapBlock;
        if (_bitmapBlocks.Count != needed || _bitmapBlocks.Any(b => b >= _blocks))
            throw new AmigaFsException("The bitmap block list is inconsistent; the volume was not opened.");
        _bitmap = _bitmapBlocks.Select(b => Read(b)).ToArray();
        if (!readOnly && _bitmap.Any(b => !AmigaChecksum.IsSumZero(b, 128)))
            throw new AmigaFsException("A bitmap block has a bad checksum. Let the Amiga validate the volume before writing from Windows.");
        _allocCursor = _root;
    }

    public static AmigaFfsVolume Open(IBlockDevice volume, bool writable)
    {
        if (volume.SectorSize != BS) throw new AmigaFsException("Only 512-byte block volumes are supported.");
        var dt = new DosType(Endian.U32BE(volume.ReadSector(0), 0));
        if (!dt.IsDosFamily) throw new AmigaFsException($"Not an OFS/FFS volume ({dt}).");
        if ((dt.Value & 0xFF) >= 6) throw new AmigaFsException($"{dt.FriendlyName} volumes are not supported yet.");
        bool dirCache = (dt.Value & 0xFF) is 4 or 5;
        return new AmigaFfsVolume(volume, dt, readOnly: !writable || dirCache);
    }

    /// <summary>True when the volume can be opened for writing by this implementation.</summary>
    public static bool SupportsWriting(DosType dt) => dt.IsDosFamily && (dt.Value & 0xFF) <= 3;

    // ----- Reading -----------------------------------------------------------------------------

    public IReadOnlyList<AmigaEntry> List(string path)
    {
        var (_, dir) = ResolveDirectory(path);
        var result = new List<AmigaEntry>();
        foreach (var blk in EnumerateDirectory(dir))
            result.Add(ToEntry(blk, Combine(path, NameOf(Read(blk)))));
        return result.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public AmigaEntry? GetEntry(string path)
    {
        var blk = Lookup(path);
        return blk is null ? null : ToEntry(blk.Value, Normalise(path));
    }

    public void ReadFile(string path, Stream output, CancellationToken ct = default)
    {
        var blk = Lookup(path) ?? throw new FileNotFoundException($"'{path}' not found on the Amiga volume.");
        var h = Read(blk);
        if (Endian.I32BE(h, BS - 4) != StFile) throw new AmigaFsException($"'{path}' is not a file.");
        long remaining = Endian.U32BE(h, BS - 188);
        foreach (var data in DataBlocks(h))
        {
            ct.ThrowIfCancellationRequested();
            if (remaining <= 0) break;
            var d = Read(data);
            if (IsFastFileSystem)
            {
                int n = (int)Math.Min(BS, remaining);
                output.Write(d, 0, n);
                remaining -= n;
            }
            else
            {
                int n = (int)Math.Min(Endian.U32BE(d, 12), Math.Min(OfsDataBytes, remaining));
                output.Write(d, 24, n);
                remaining -= n;
            }
        }
        if (remaining > 0) throw new AmigaFsException($"'{path}' is shorter on disk than its recorded size; the volume may need validating.");
    }

    // ----- Writing -----------------------------------------------------------------------------

    public AmigaEntry WriteFile(string path, Stream content, DateTime modified, uint protection = 0, string comment = "", bool overwrite = false, CancellationToken ct = default)
    {
        EnsureWritable();
        var (parentPath, name) = Split(path);
        ValidateName(name);
        if (comment.Length > 79) throw new AmigaFsException("Comments are limited to 79 characters.");
        var (parentBlock, _) = ResolveDirectory(parentPath);

        var existing = FindInDirectory(parentBlock, name);
        if (existing is not null)
        {
            if (!overwrite) throw new IOException($"'{Normalise(path)}' already exists.");
            if (Endian.I32BE(Read(existing.Value), BS - 4) != StFile) throw new AmigaFsException($"'{name}' is a directory.");
            Delete(Combine(parentPath, name));
        }

        long length = content.CanSeek ? content.Length - content.Position : -1;
        var buffered = length < 0 ? CopyToMemory(content) : null;
        if (buffered is not null) { content = buffered; length = buffered.Length; }

        int perBlock = IsFastFileSystem ? BS : OfsDataBytes;
        long dataCount = (length + perBlock - 1) / perBlock;
        long extCount = dataCount > HashSize ? (dataCount - HashSize + HashSize - 1) / HashSize : 0;
        if (1 + dataCount + extCount > CountFree()) throw new IOException($"Not enough space on '{VolumeName}' for '{name}'.");

        BeginChanges();
        uint header = Allocate();
        var data = new uint[dataCount];
        for (long i = 0; i < dataCount; i++) data[i] = Allocate();
        var exts = new uint[extCount];
        for (long i = 0; i < extCount; i++) exts[i] = Allocate();

        // Data blocks.
        var buf = new byte[perBlock];
        for (long i = 0; i < dataCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            int n = ReadFull(content, buf);
            var b = new byte[BS];
            if (IsFastFileSystem) buf.AsSpan(0, n).CopyTo(b);
            else
            {
                Endian.PutU32BE(b, 0, TypeData);
                Endian.PutU32BE(b, 4, header);
                Endian.PutU32BE(b, 8, (uint)(i + 1));
                Endian.PutU32BE(b, 12, (uint)n);
                Endian.PutU32BE(b, 16, i + 1 < dataCount ? data[i + 1] : 0);
                buf.AsSpan(0, n).CopyTo(b.AsSpan(24));
                AmigaFfsFormatter.SetHeaderChecksum(b);
            }
            Write(data[i], b);
        }

        // File header with the first 72 data pointers, then extension blocks for the rest.
        var h = new byte[BS];
        Endian.PutU32BE(h, 0, TypeHeader);
        Endian.PutU32BE(h, 4, header);
        int firstCount = (int)Math.Min(HashSize, dataCount);
        Endian.PutU32BE(h, 8, (uint)firstCount);
        Endian.PutU32BE(h, 16, dataCount > 0 ? data[0] : 0);
        for (int i = 0; i < firstCount; i++) Endian.PutU32BE(h, BS - 204 - i * 4, data[i]);
        Endian.PutU32BE(h, BS - 192, protection);
        Endian.PutU32BE(h, BS - 188, (uint)length);
        Endian.PutBcplString(h, BS - 184, comment, 79);
        PutDate(h, BS - 92, AmigaDate.From(modified));
        Endian.PutBcplString(h, BS - 80, name, 30);
        Endian.PutU32BE(h, BS - 12, parentBlock);
        Endian.PutU32BE(h, BS - 8, extCount > 0 ? exts[0] : 0);
        Endian.PutI32BE(h, BS - 4, StFile);
        AmigaFfsFormatter.SetHeaderChecksum(h);
        Write(header, h);

        for (long e = 0; e < extCount; e++)
        {
            var x = new byte[BS];
            long start = HashSize + e * HashSize;
            int count = (int)Math.Min(HashSize, dataCount - start);
            Endian.PutU32BE(x, 0, TypeList);
            Endian.PutU32BE(x, 4, exts[e]);
            Endian.PutU32BE(x, 8, (uint)count);
            for (int i = 0; i < count; i++) Endian.PutU32BE(x, BS - 204 - i * 4, data[start + i]);
            Endian.PutU32BE(x, BS - 12, header);
            Endian.PutU32BE(x, BS - 8, e + 1 < extCount ? exts[e + 1] : 0);
            Endian.PutI32BE(x, BS - 4, StFile);
            AmigaFfsFormatter.SetHeaderChecksum(x);
            Write(exts[e], x);
        }

        LinkIntoDirectory(parentBlock, header, name);
        return ToEntry(header, Combine(parentPath, name));
    }

    public AmigaEntry CreateDirectory(string path, DateTime? created = null)
    {
        EnsureWritable();
        var (parentPath, name) = Split(path);
        ValidateName(name);
        var (parentBlock, _) = ResolveDirectory(parentPath);
        if (FindInDirectory(parentBlock, name) is { } existing)
        {
            if (Endian.I32BE(Read(existing), BS - 4) == StUserDir) return ToEntry(existing, Combine(parentPath, name));
            throw new IOException($"A file named '{name}' already exists.");
        }
        if (CountFree() < 1) throw new IOException($"'{VolumeName}' is full.");

        BeginChanges();
        uint blk = Allocate();
        var d = new byte[BS];
        Endian.PutU32BE(d, 0, TypeHeader);
        Endian.PutU32BE(d, 4, blk);
        PutDate(d, BS - 92, AmigaDate.From(created ?? DateTime.Now));
        Endian.PutBcplString(d, BS - 80, name, 30);
        Endian.PutU32BE(d, BS - 12, parentBlock);
        Endian.PutI32BE(d, BS - 4, StUserDir);
        AmigaFfsFormatter.SetHeaderChecksum(d);
        Write(blk, d);
        LinkIntoDirectory(parentBlock, blk, name);
        return ToEntry(blk, Combine(parentPath, name));
    }

    /// <summary>Deletes a file or an empty directory.</summary>
    public void Delete(string path)
    {
        EnsureWritable();
        var (parentPath, name) = Split(path);
        var (parentBlock, _) = ResolveDirectory(parentPath);
        uint blk = FindInDirectory(parentBlock, name) ?? throw new FileNotFoundException($"'{Normalise(path)}' not found.");
        var h = Read(blk);
        int st = Endian.I32BE(h, BS - 4);
        if (st == StUserDir && EnumerateDirectory(h).Any()) throw new IOException($"Directory '{name}' is not empty.");
        if (st != StUserDir && st != StFile) throw new AmigaFsException($"'{name}' is a link; links cannot be deleted from Windows.");
        if ((Endian.U32BE(h, BS - 192) & 1) != 0) throw new IOException($"'{name}' is protected from deletion (the 'd' bit is cleared).");

        BeginChanges();
        UnlinkFromDirectory(parentBlock, blk, name);
        if (st == StFile)
        {
            foreach (var d in DataBlocks(h)) Free(d);
            var seen = new HashSet<uint>();
            for (uint ext = Endian.U32BE(h, BS - 8); ext != 0 && ext < _blocks && seen.Add(ext);)
            {
                var x = Read(ext);
                Free(ext);
                ext = Endian.U32BE(x, BS - 8);
            }
        }
        Free(blk);
    }

    /// <summary>Writes the bitmap and marks it valid. Must be called before the device is released.</summary>
    public void Flush()
    {
        if (!_dirty) return;
        for (int i = 0; i < _bitmap.Length; i++)
        {
            AmigaChecksum.SetSumToZero(_bitmap[i], 128, 0);
            Write(_bitmapBlocks[i], _bitmap[i]);
        }
        _dev.Flush();
        var r = Read(_root);
        Endian.PutU32BE(r, BS - 200, 0xFFFFFFFF);
        var now = AmigaDate.From(DateTime.Now);
        PutDate(r, BS - 92, now);
        PutDate(r, BS - 40, now);
        AmigaFfsFormatter.SetHeaderChecksum(r);
        Write(_root, r);
        _dev.Flush();
        _dirty = false;
    }

    public void Dispose() => Flush();

    // ----- Internals ---------------------------------------------------------------------------

    private void EnsureWritable()
    {
        if (IsReadOnly) throw new AmigaFsException($"'{VolumeName}' is open read-only ({DosType.FriendlyName} volumes cannot be written by this tool).");
    }

    private void BeginChanges()
    {
        if (_dirty) return;
        var r = Read(_root);
        Endian.PutU32BE(r, BS - 200, 0); // bitmap invalid until Flush completes
        AmigaFfsFormatter.SetHeaderChecksum(r);
        Write(_root, r);
        _dev.Flush();
        _dirty = true;
    }

    private (uint Block, byte[] Data) ResolveDirectory(string path)
    {
        uint blk = _root;
        var data = Read(blk);
        foreach (var part in Parts(path))
        {
            blk = FindInDirectory(blk, part) ?? throw new DirectoryNotFoundException($"'{part}' not found.");
            data = Read(blk);
            if (Endian.I32BE(data, BS - 4) != StUserDir) throw new DirectoryNotFoundException($"'{part}' is not a directory.");
        }
        return (blk, data);
    }

    private uint? Lookup(string path)
    {
        var parts = Parts(path).ToList();
        if (parts.Count == 0) return _root;
        var (parentPath, name) = Split(path);
        var (parent, _) = ResolveDirectory(parentPath);
        return FindInDirectory(parent, name);
    }

    private uint? FindInDirectory(uint dirBlock, string name)
    {
        var dir = Read(dirBlock);
        uint blk = Endian.U32BE(dir, 24 + Hash(name) * 4);
        var seen = new HashSet<uint>();
        while (blk != 0 && blk < _blocks && seen.Add(blk))
        {
            var h = Read(blk);
            if (NamesEqual(NameOf(h), name)) return blk;
            blk = Endian.U32BE(h, BS - 16);
        }
        return null;
    }

    private IEnumerable<uint> EnumerateDirectory(byte[] dir)
    {
        for (int i = 0; i < HashSize; i++)
        {
            uint blk = Endian.U32BE(dir, 24 + i * 4);
            var seen = new HashSet<uint>();
            while (blk != 0 && blk < _blocks && seen.Add(blk))
            {
                yield return blk;
                blk = Endian.U32BE(Read(blk), BS - 16);
            }
        }
    }

    /// <summary>Inserts into the hash chain, keeping the chain in ascending block order.</summary>
    private void LinkIntoDirectory(uint dirBlock, uint entry, string name)
    {
        var dir = Read(dirBlock);
        int slot = 24 + Hash(name) * 4;
        uint first = Endian.U32BE(dir, slot);
        if (first == 0 || entry < first)
        {
            SetChain(entry, first);
            Endian.PutU32BE(dir, slot, entry);
        }
        else
        {
            uint prev = first;
            while (true)
            {
                var p = Read(prev);
                uint next = Endian.U32BE(p, BS - 16);
                if (next == 0 || entry < next)
                {
                    SetChain(entry, next);
                    SetChain(prev, entry);
                    break;
                }
                prev = next;
            }
            dir = Read(dirBlock);
        }
        Touch(dir);
        Write(dirBlock, dir);
    }

    private void UnlinkFromDirectory(uint dirBlock, uint entry, string name)
    {
        var dir = Read(dirBlock);
        int slot = 24 + Hash(name) * 4;
        uint next = Endian.U32BE(Read(entry), BS - 16);
        uint cur = Endian.U32BE(dir, slot);
        if (cur == entry) Endian.PutU32BE(dir, slot, next);
        else
        {
            while (cur != 0)
            {
                var c = Read(cur);
                uint n = Endian.U32BE(c, BS - 16);
                if (n == entry) { SetChain(cur, next); break; }
                cur = n;
            }
            dir = Read(dirBlock);
        }
        Touch(dir);
        Write(dirBlock, dir);
    }

    private void SetChain(uint blk, uint next)
    {
        var b = Read(blk);
        Endian.PutU32BE(b, BS - 16, next);
        AmigaFfsFormatter.SetHeaderChecksum(b);
        Write(blk, b);
    }

    private void Touch(byte[] dirBlock)
    {
        PutDate(dirBlock, BS - 92, AmigaDate.From(DateTime.Now));
        AmigaFfsFormatter.SetHeaderChecksum(dirBlock);
    }

    private IEnumerable<uint> DataBlocks(byte[] header)
    {
        var block = header;
        var seen = new HashSet<uint>();
        while (true)
        {
            int count = (int)Math.Min(Endian.U32BE(block, 8), HashSize);
            for (int i = 0; i < count; i++)
            {
                uint d = Endian.U32BE(block, BS - 204 - i * 4);
                if (d == 0 || d >= _blocks) yield break;
                yield return d;
            }
            uint ext = Endian.U32BE(block, BS - 8);
            if (ext == 0 || ext >= _blocks || !seen.Add(ext)) yield break;
            block = Read(ext);
        }
    }

    private AmigaEntry ToEntry(uint blk, string path)
    {
        var h = Read(blk);
        int st = Endian.I32BE(h, BS - 4);
        return new AmigaEntry
        {
            Name = NameOf(h),
            Path = path,
            IsDirectory = st == StUserDir || st == StRoot,
            Size = st == StFile ? Endian.U32BE(h, BS - 188) : 0,
            Modified = new AmigaDate(Endian.U32BE(h, BS - 92), Endian.U32BE(h, BS - 88), Endian.U32BE(h, BS - 84)).ToDateTime(),
            Protection = st == StRoot ? 0 : Endian.U32BE(h, BS - 192),
            Comment = st == StRoot ? "" : Endian.BcplString(h, BS - 184, 79),
            HeaderBlock = blk,
        };
    }

    // ----- Bitmap ------------------------------------------------------------------------------

    private bool IsFree(uint blk)
    {
        long bit = blk - 2L;
        var b = _bitmap[bit / AmigaFfsFormatter.BitsPerBitmapBlock];
        long inBlock = bit % AmigaFfsFormatter.BitsPerBitmapBlock;
        uint word = Endian.U32BE(b, 4 + (int)(inBlock / 32) * 4);
        return (word >> (int)(inBlock % 32) & 1) != 0;
    }

    private void SetFree(uint blk, bool free)
    {
        long bit = blk - 2L;
        var b = _bitmap[bit / AmigaFfsFormatter.BitsPerBitmapBlock];
        long inBlock = bit % AmigaFfsFormatter.BitsPerBitmapBlock;
        int off = 4 + (int)(inBlock / 32) * 4;
        uint word = Endian.U32BE(b, off);
        uint mask = 1u << (int)(inBlock % 32);
        Endian.PutU32BE(b, off, free ? word | mask : word & ~mask);
    }

    /// <summary>Allocates the next free block after the previous allocation (AmigaDOS also allocates upwards from the root).</summary>
    private uint Allocate()
    {
        long span = _blocks - 2;
        for (long i = 0; i < span; i++)
        {
            uint candidate = (uint)(2 + (_allocCursor - 2 + i) % span);
            if (IsFree(candidate))
            {
                SetFree(candidate, false);
                _allocCursor = candidate;
                return candidate;
            }
        }
        throw new IOException($"'{VolumeName}' is full.");
    }

    private void Free(uint blk)
    {
        if (blk < 2 || blk >= _blocks || blk == _root) return;
        SetFree(blk, true);
    }

    private long CountFree()
    {
        long free = 0, bits = _blocks - 2;
        for (int i = 0; i < _bitmap.Length; i++)
            for (int l = 0; l < 127; l++)
            {
                uint w = Endian.U32BE(_bitmap[i], 4 + l * 4);
                long first = (long)i * AmigaFfsFormatter.BitsPerBitmapBlock + l * 32;
                if (first >= bits) return free;
                if (first + 32 > bits) w &= (1u << (int)(bits - first)) - 1;
                free += System.Numerics.BitOperations.PopCount(w);
            }
        return free;
    }

    // ----- Names -------------------------------------------------------------------------------

    private int Hash(string name)
    {
        uint h = (uint)name.Length;
        foreach (char c in name) h = (h * 13 + ToUpper(c)) & 0x7FF;
        return (int)(h % HashSize);
    }

    private char ToUpper(char c)
    {
        if (c is >= 'a' and <= 'z') return (char)(c - 32);
        if (Intl && c is >= (char)0xE0 and <= (char)0xFE && c != (char)0xF7) return (char)(c - 32);
        return c;
    }

    private bool NamesEqual(string a, string b) =>
        a.Length == b.Length && a.Zip(b).All(p => ToUpper(p.First) == ToUpper(p.Second));

    public static void ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 30) throw new AmigaFsException($"'{name}': Amiga file names must be 1-30 characters.");
        if (name.Any(c => c is ':' or '/' || c < ' ' || c > 0xFF)) throw new AmigaFsException($"'{name}' contains a character AmigaDOS does not allow.");
    }

    private static string NameOf(byte[] h) => Endian.BcplString(h, BS - 80, 30);

    private static IEnumerable<string> Parts(string path) =>
        Normalise(path).Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static string Normalise(string path)
    {
        var p = path.Replace('\\', '/');
        int colon = p.IndexOf(':');
        if (colon >= 0) p = p[(colon + 1)..];
        return p.Trim('/');
    }

    private static (string Parent, string Name) Split(string path)
    {
        var p = Normalise(path);
        int i = p.LastIndexOf('/');
        return i < 0 ? ("", p) : (p[..i], p[(i + 1)..]);
    }

    private static string Combine(string parent, string name)
    {
        var p = Normalise(parent);
        return p.Length == 0 ? name : $"{p}/{name}";
    }

    // ----- Block I/O ---------------------------------------------------------------------------

    private byte[] Read(long blk) => _dev.ReadSector(blk);

    private void Write(long blk, byte[] data)
    {
        if (blk < 2 || blk >= _blocks) throw new AmigaFsException($"Refusing to write outside the volume (block {blk}).");
        _dev.WriteSector(blk, data);
    }

    private static void PutDate(Span<byte> b, int offset, AmigaDate d)
    {
        Endian.PutU32BE(b, offset, d.Days);
        Endian.PutU32BE(b, offset + 4, d.Minutes);
        Endian.PutU32BE(b, offset + 8, d.Ticks);
    }

    private static int ReadFull(Stream s, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = s.Read(buffer, total, buffer.Length - total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    private static MemoryStream CopyToMemory(Stream s)
    {
        var ms = new MemoryStream();
        s.CopyTo(ms);
        ms.Position = 0;
        return ms;
    }
}
