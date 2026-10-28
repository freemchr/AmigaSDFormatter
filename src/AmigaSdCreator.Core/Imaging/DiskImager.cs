using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using AmigaSdCreator.Core.IO;
using AmigaSdCreator.Core.Profiles;

namespace AmigaSdCreator.Core.Imaging;

public sealed record ImagingProgress(string Stage, long BytesDone, long BytesTotal, double BytesPerSecond)
{
    public double Fraction => BytesTotal <= 0 ? 0 : (double)BytesDone / BytesTotal;
}

/// <summary>Sidecar file written next to every backup (image.img.json).</summary>
public sealed record ImageManifest
{
    public int ManifestVersion { get; init; } = 1;
    public required string Compression { get; init; }
    public required long SizeBytes { get; init; }
    public required int SectorSize { get; init; }
    public required string Sha256 { get; init; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public string SourceDescription { get; init; } = "";
    public string Application { get; init; } = "Amiga SD Creator";
}

public sealed record ImageInfo(string Path, bool Compressed, long SizeBytes, string Sha256, ImageManifest? Manifest)
{
    public bool ManifestMatches => Manifest is null || (Manifest.Sha256.Equals(Sha256, StringComparison.OrdinalIgnoreCase) && Manifest.SizeBytes == SizeBytes);
}

public sealed class ImagingException(string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>True when the target device was partially written and is now in an undefined state.</summary>
    public bool TargetModified { get; init; }
}

/// <summary>Full-device backup and restore with SHA-256 integrity checks and read-back verification.</summary>
public static class DiskImager
{
    public const int ChunkBytes = 4 * 1024 * 1024;

    public static string ManifestPath(string imagePath) => imagePath + ".json";
    public static bool IsCompressedPath(string path) => path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the whole device into an image file, then re-reads the image to confirm its hash.
    /// The partial file is deleted if anything fails.
    /// </summary>
    public static async Task<ImageManifest> BackupAsync(IBlockDevice source, string imagePath, IProgress<ImagingProgress>? progress = null, CancellationToken ct = default)
    {
        bool compress = IsCompressedPath(imagePath);
        string temp = imagePath + ".partial";
        string hash;
        try
        {
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
                await using (Stream output = compress ? new GZipStream(file, CompressionLevel.Optimal) : file)
                {
                    var buffer = new byte[ChunkBytes];
                    var timer = Stopwatch.StartNew();
                    long done = 0, total = source.Length;
                    while (done < total)
                    {
                        ct.ThrowIfCancellationRequested();
                        int n = (int)Math.Min(buffer.Length, total - done);
                        try { source.Read(done, buffer.AsSpan(0, n)); }
                        catch (IOException ex) { throw new ImagingException($"Reading the card failed at byte {done:N0}. The card may have been removed or has a read error.", ex); }
                        sha.AppendData(buffer, 0, n);
                        await output.WriteAsync(buffer.AsMemory(0, n), ct);
                        done += n;
                        progress?.Report(new ImagingProgress("Reading card", done, total, done / Math.Max(timer.Elapsed.TotalSeconds, 0.001)));
                    }
                }
                hash = Convert.ToHexString(sha.GetHashAndReset());
            }

            var verify = await HashImageAsync(temp, compress, progress, ct);
            if (!verify.Hash.Equals(hash, StringComparison.Ordinal) || verify.Size != source.Length)
                throw new ImagingException("The backup file did not read back correctly. The disk holding the backup may be faulty or full.");

            File.Move(temp, imagePath, overwrite: true);
            var manifest = new ImageManifest
            {
                Compression = compress ? "gzip" : "none",
                SizeBytes = source.Length,
                SectorSize = source.SectorSize,
                Sha256 = hash,
                SourceDescription = source.Description,
            };
            await File.WriteAllTextAsync(ManifestPath(imagePath), JsonSerializer.Serialize(manifest, ProfileCatalog.JsonOptions), ct);
            return manifest;
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Hashes an image (decompressing if needed) and loads its manifest, if any.</summary>
    public static async Task<ImageInfo> InspectImageAsync(string imagePath, IProgress<ImagingProgress>? progress = null, CancellationToken ct = default)
    {
        bool compressed = IsCompressedPath(imagePath) || await HasGzipMagicAsync(imagePath, ct);
        var (hash, size) = await HashImageAsync(imagePath, compressed, progress, ct);
        ImageManifest? manifest = null;
        var mp = ManifestPath(imagePath);
        if (File.Exists(mp))
        {
            try { manifest = JsonSerializer.Deserialize<ImageManifest>(await File.ReadAllTextAsync(mp, ct), ProfileCatalog.JsonOptions); }
            catch (JsonException) { manifest = null; }
        }
        return new ImageInfo(imagePath, compressed, size, hash, manifest);
    }

    /// <summary>
    /// Writes an image to the target and verifies by reading the written range back. The image must
    /// already have been inspected (<paramref name="image"/>), which is its integrity check.
    /// </summary>
    public static async Task RestoreAsync(ImageInfo image, IBlockDevice target, IProgress<ImagingProgress>? progress = null, CancellationToken ct = default)
    {
        if (!image.ManifestMatches)
            throw new ImagingException("The image does not match its manifest (size or SHA-256 differs). It may be damaged; it was not written.");
        long padded = (image.SizeBytes + target.SectorSize - 1) / target.SectorSize * target.SectorSize;
        if (padded > target.Length)
            throw new ImagingException($"The image ({image.SizeBytes:N0} bytes) is larger than the target ({target.Length:N0} bytes). Nothing was written.");

        long done = 0;
        var timer = Stopwatch.StartNew();
        try
        {
            await using var file = new FileStream(image.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
            await using Stream input = image.Compressed ? new GZipStream(file, CompressionMode.Decompress) : file;
            var buffer = new byte[ChunkBytes];
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int n = await ReadFullAsync(input, buffer, ct);
                if (n == 0) break;
                int aligned = (n + target.SectorSize - 1) / target.SectorSize * target.SectorSize;
                if (aligned > n) Array.Clear(buffer, n, aligned - n);
                target.Write(done, buffer.AsSpan(0, aligned));
                done += n;
                progress?.Report(new ImagingProgress("Writing card", done, image.SizeBytes, done / Math.Max(timer.Elapsed.TotalSeconds, 0.001)));
            }
            target.Flush();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            if (done == 0 && ex is OperationCanceledException) throw;
            throw new ImagingException(
                $"Writing stopped after {done:N0} of {image.SizeBytes:N0} bytes ({ex.Message}). The card now contains an incomplete image and will not work until it is written again.", ex)
            { TargetModified = done > 0 };
        }

        progress?.Report(new ImagingProgress("Verifying", 0, image.SizeBytes, 0));
        string written = await HashDeviceRangeAsync(target, image.SizeBytes, progress, ct);
        if (!written.Equals(image.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new ImagingException("Verification failed: the data read back from the card differs from the image. The card may be counterfeit (fake capacity) or failing.") { TargetModified = true };
    }

    public static async Task<string> HashDeviceRangeAsync(IBlockDevice device, long length, IProgress<ImagingProgress>? progress, CancellationToken ct)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[ChunkBytes];
        long done = 0;
        var timer = Stopwatch.StartNew();
        while (done < length)
        {
            ct.ThrowIfCancellationRequested();
            int want = (int)Math.Min(buffer.Length, length - done);
            int aligned = (want + device.SectorSize - 1) / device.SectorSize * device.SectorSize;
            device.Read(done, buffer.AsSpan(0, aligned));
            sha.AppendData(buffer, 0, want);
            done += want;
            progress?.Report(new ImagingProgress("Verifying", done, length, done / Math.Max(timer.Elapsed.TotalSeconds, 0.001)));
            await Task.Yield();
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    private static async Task<(string Hash, long Size)> HashImageAsync(string path, bool compressed, IProgress<ImagingProgress>? progress, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        long fileLength = file.Length;
        await using Stream input = compressed ? new GZipStream(file, CompressionMode.Decompress) : file;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[ChunkBytes];
        long size = 0;
        var timer = Stopwatch.StartNew();
        int n;
        while ((n = await input.ReadAsync(buffer, ct)) > 0)
        {
            sha.AppendData(buffer, 0, n);
            size += n;
            // For gzip the total is unknown; report compressed position against compressed length.
            long pos = compressed ? file.Position : size;
            progress?.Report(new ImagingProgress("Checking image", pos, fileLength, size / Math.Max(timer.Elapsed.TotalSeconds, 0.001)));
        }
        return (Convert.ToHexString(sha.GetHashAndReset()), size);
    }

    private static async Task<int> ReadFullAsync(Stream s, byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await s.ReadAsync(buffer.AsMemory(total), ct);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    private static async Task<bool> HasGzipMagicAsync(string path, CancellationToken ct)
    {
        await using var f = File.OpenRead(path);
        var b = new byte[2];
        return await f.ReadAsync(b, ct) == 2 && b[0] == 0x1F && b[1] == 0x8B;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
