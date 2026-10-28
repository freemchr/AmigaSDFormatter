using System.IO;
using AmigaSdCreator.Core.FileSystems;
using AmigaSdCreator.Core.Layout;

namespace AmigaSdCreator.App.Services;

/// <summary>
/// The right-hand pane of the file manager: either an Amiga OFS/FFS partition accessed directly, or a
/// FAT32 partition that Windows has mounted (accessed through its drive letter).
/// Paths use '/' separators relative to the volume root.
/// </summary>
public interface ITargetVolume : IDisposable
{
    string VolumeName { get; }
    string KindText { get; }
    long FreeBytes { get; }
    long TotalBytes { get; }
    bool IsReadOnly { get; }
    IReadOnlyList<AmigaEntry> List(string path);
    void ReadFile(string path, Stream output, CancellationToken ct);
    void WriteFile(string path, Stream content, long length, DateTime modified, bool overwrite, CancellationToken ct);
    void CreateDirectory(string path, DateTime? created);
    void Delete(string path);
    void Flush();
    /// <summary>Throws if <paramref name="name"/> cannot be used on this volume.</summary>
    void ValidateName(string name);
}

/// <summary>An Amiga OFS/FFS partition written by this app's own filesystem engine.</summary>
public sealed class FfsTargetVolume(AmigaFfsVolume volume) : ITargetVolume
{
    public string VolumeName => volume.VolumeName;
    public string KindText => volume.DosType.FriendlyName;
    public long FreeBytes => volume.FreeBytes;
    public long TotalBytes => volume.TotalBytes;
    public bool IsReadOnly => volume.IsReadOnly;
    public IReadOnlyList<AmigaEntry> List(string path) => volume.List(path);
    public void ReadFile(string path, Stream output, CancellationToken ct) => volume.ReadFile(path, output, ct);
    public void WriteFile(string path, Stream content, long length, DateTime modified, bool overwrite, CancellationToken ct) =>
        // Cancellation is honoured between files: stopping inside an FFS write would leave blocks allocated.
        volume.WriteFile(path, content, modified, overwrite: overwrite);
    public void CreateDirectory(string path, DateTime? created) => volume.CreateDirectory(path, created);
    public void Delete(string path) => volume.Delete(path);
    public void Flush() => volume.Flush();
    public void ValidateName(string name) => AmigaFfsVolume.ValidateName(name);
    public void Dispose() => volume.Dispose();
}

/// <summary>A FAT32 partition mounted by Windows (e.g. an A6000 "SD0" transfer card), used through normal file I/O.</summary>
public sealed class FolderTargetVolume(string root, string label, bool readOnly) : ITargetVolume
{
    private const long Fat32MaxFileBytes = uint.MaxValue; // 4 GiB - 1

    public string VolumeName => label;
    public string KindText => "FAT32 (via Windows)";
    public long FreeBytes => new DriveInfo(root).AvailableFreeSpace;
    public long TotalBytes => new DriveInfo(root).TotalSize;
    public bool IsReadOnly => readOnly;

    private string Full(string path) => Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));

    public IReadOnlyList<AmigaEntry> List(string path)
    {
        var dir = new DirectoryInfo(Full(path));
        string Rel(string name) => path.Length == 0 ? name : $"{path}/{name}";
        return dir.EnumerateFileSystemInfos()
            .Where(i => (i.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
            .Select(i => new AmigaEntry
            {
                Name = i.Name,
                Path = Rel(i.Name),
                IsDirectory = i is DirectoryInfo,
                Size = i is FileInfo f ? f.Length : 0,
                Modified = i.LastWriteTime,
            })
            .OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void ReadFile(string path, Stream output, CancellationToken ct)
    {
        using var input = File.OpenRead(Full(path));
        Copy(input, output, ct);
    }

    public void WriteFile(string path, Stream content, long length, DateTime modified, bool overwrite, CancellationToken ct)
    {
        if (readOnly) throw new IOException("The volume is open read-only.");
        if (length > Fat32MaxFileBytes) throw new IOException($"{Format.Bytes(length)} is larger than the 4 GB per-file limit of FAT32.");
        string dest = Full(path);
        if (File.Exists(dest) && !overwrite) throw new IOException("Already exists on the card.");
        try
        {
            using (var output = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                Copy(content, output, ct);
            File.SetLastWriteTime(dest, modified);
        }
        catch (OperationCanceledException)
        {
            TryDelete(dest); // never leave a half-copied file behind
            throw;
        }
    }

    public void CreateDirectory(string path, DateTime? created)
    {
        if (readOnly) throw new IOException("The volume is open read-only.");
        Directory.CreateDirectory(Full(path));
    }

    public void Delete(string path)
    {
        if (readOnly) throw new IOException("The volume is open read-only.");
        string full = Full(path);
        if (Directory.Exists(full)) Directory.Delete(full, recursive: false);
        else File.Delete(full);
    }

    public void Flush() { }

    public void ValidateName(string name)
    {
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new IOException($"'{name}' contains characters FAT does not allow.");
    }

    public void Dispose() { }

    private static void Copy(Stream input, Stream output, CancellationToken ct)
    {
        var buffer = new byte[1 << 20];
        int n;
        while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            output.Write(buffer, 0, n);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
