using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using MediaEngine.Contracts.Reading;
using MediaEngine.Domain.Configuration;
using SharpCompress.Archives;
using SharpCompress.Common;
using SkiaSharp;
using SharpCompressReaderOptions = SharpCompress.Readers.ReaderOptions;

namespace MediaEngine.Api.Services.Reading;

/// <summary>
/// PDF and comic reader spec D2, D4, D16, D17, D18. A zip comic is read in place, entry by entry; a RAR or 7-Zip comic is
/// unpacked once into a size-limited temporary folder (nothing is written beside the person's files). Only picture entries of
/// known kinds are ever returned.
/// </summary>
public sealed class ComicPageService : IComicPageService
{
    private static readonly HashSet<string> PictureExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp",
    };

    private const int SniffBytes = 192 * 1024;
    private const int MaxRememberedComics = 24;
    private const int MaxInfoFileBytes = 1024 * 1024;

    private readonly Func<ReaderConfiguration> _configuration;
    private readonly string _cacheRoot;
    private readonly ConcurrentDictionary<string, Task<ComicSource>> _sources = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastUsed = new();
    private readonly SemaphoreSlim _evictionGate = new(1, 1);

    public ComicPageService(ReaderConfigurationProvider configuration, string? cacheRoot = null)
        : this(() => configuration.Current, cacheRoot)
    {
    }

    public ComicPageService(Func<ReaderConfiguration> configuration, string? cacheRoot = null)
    {
        _configuration = configuration;
        _cacheRoot = cacheRoot ?? Path.Combine(Path.GetTempPath(), "tuvima-comic-pages");
    }

    public async Task<ComicPageListDto> GetPageListAsync(string filePath, CancellationToken ct)
    {
        var source = await GetSourceAsync(filePath, ct);
        return new ComicPageListDto
        {
            PageCount = source.Pages.Count,
            RightToLeft = source.RightToLeft,
            Widths = _configuration().Comic.Widths,
            Pages = source.Pages.Select((page, index) => new ComicPageDto
            {
                Number = index + 1,
                Width = page.Width,
                Height = page.Height,
            }).ToList(),
        };
    }

    public async Task<ComicPageImage> GetPageAsync(string filePath, int page, int width, CancellationToken ct)
    {
        var settings = _configuration().Comic;
        if (width != 0 && !settings.Widths.Contains(width))
        {
            throw new ComicPageException(ComicPageFailure.BadWidth, "That page size is not available.");
        }

        var source = await GetSourceAsync(filePath, ct);
        if (page < 1 || page > source.Pages.Count)
        {
            throw new ComicPageException(ComicPageFailure.NoSuchPage, "There is no such page.");
        }

        var entry = source.Pages[page - 1];
        var bytes = await Task.Run(() => source.ReadPage(entry, settings), ct);
        return await Task.Run(() => MakePicture(bytes, entry.Extension, width, settings), ct);
    }

    // ── Opening a comic ──────────────────────────────────────────────────

    private async Task<ComicSource> GetSourceAsync(string filePath, CancellationToken ct)
    {
        FileInfo file;
        try
        {
            file = new FileInfo(filePath);
            if (!file.Exists)
            {
                throw new ComicPageException(ComicPageFailure.Unreadable, "The file is missing.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new ComicPageException(ComicPageFailure.Unreadable, "The file can't be read.");
        }

        var key = $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
        _lastUsed[key] = DateTime.UtcNow;
        var task = _sources.GetOrAdd(key, _ => Task.Run(() => OpenSource(file, key), CancellationToken.None));
        try
        {
            var source = await task.WaitAsync(ct);
            _ = TrimRememberedAsync(key);
            return source;
        }
        catch (ComicPageException)
        {
            _sources.TryRemove(key, out _); // a damaged or refused comic is looked at again next time, not remembered as good
            throw;
        }
    }

    private ComicSource OpenSource(FileInfo file, string key)
    {
        var settings = _configuration().Comic;
        try
        {
            return IsZip(file.FullName) ? OpenZip(file, settings) : OpenUnpacked(file, key, settings);
        }
        catch (ComicPageException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArchiveException or InvalidOperationException
                                       or NotSupportedException or UnauthorizedAccessException or XmlException or FormatException
                                       or ExtractionException or InvalidFormatException or SharpCompressException)
        {
            throw new ComicPageException(ComicPageFailure.Unreadable, "This file can't be opened.");
        }
    }

    private static bool IsZip(string path)
    {
        Span<byte> header = stackalloc byte[4];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return stream.Read(header) == 4 && header[0] == (byte)'P' && header[1] == (byte)'K';
    }

    private ComicSource OpenZip(FileInfo file, ComicReaderConfiguration settings)
    {
        using var zip = ZipFile.OpenRead(file.FullName);
        var candidates = new List<string>();
        ZipArchiveEntry? infoEntry = null;
        foreach (var entry in zip.Entries)
        {
            if (candidates.Count > settings.MaxEntries)
            {
                throw new ComicPageException(ComicPageFailure.Refused, "This file is too large to open.");
            }

            if (IsComicInfo(entry.FullName))
            {
                infoEntry ??= entry;
            }
            else if (IsPicture(entry.FullName) && !entry.FullName.EndsWith('/'))
            {
                candidates.Add(entry.FullName);
            }
        }

        EnsureNotEmpty(candidates);
        var ordered = candidates.OrderBy(name => name, NaturalComparer.Instance).ToList();
        var info = infoEntry is null ? ComicInfo.Empty : ReadInfo(infoEntry.Open());
        var pages = new List<PageEntry>(ordered.Count);
        foreach (var name in ordered)
        {
            var entry = zip.GetEntry(name)!;
            if (entry.Length > settings.MaxPageMegabytes * 1024L * 1024L)
            {
                throw new ComicPageException(ComicPageFailure.Refused, "This file is too large to open.");
            }

            var (width, height) = ReadSize(() => entry.Open(), settings);
            pages.Add(new PageEntry(name, Path.GetExtension(name), width, height));
        }

        return new ComicSource(file.FullName, FrontCoverFirst(pages, info), info.RightToLeft, null);
    }

    private ComicSource OpenUnpacked(FileInfo file, string key, ComicReaderConfiguration settings)
    {
        var directory = Path.Combine(_cacheRoot, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32]);
        var manifestPath = Path.Combine(directory, "pages.json");
        if (File.Exists(manifestPath))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<UnpackedManifest>(File.ReadAllText(manifestPath));
                if (cached is not null && cached.Pages.Count > 0 && cached.Pages.All(page => File.Exists(Path.Combine(directory, page.Name))))
                {
                    Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow);
                    return new ComicSource(file.FullName, cached.Pages.Select(p => new PageEntry(p.Name, p.Extension, p.Width, p.Height)).ToList(),
                        cached.RightToLeft, directory);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // A damaged cache entry is simply unpacked again below.
                System.Diagnostics.Trace.TraceWarning($"Comic page cache entry could not be read ({ex.GetType().Name}); unpacking again.");
            }

            TryDelete(directory);
        }

        using var archive = ArchiveFactory.OpenArchive(file.FullName, new SharpCompressReaderOptions());
        var entries = new List<IArchiveEntry>();
        IArchiveEntry? infoEntry = null;
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.IsDirectory || entry.Key is null)
            {
                continue;
            }

            if (IsComicInfo(entry.Key))
            {
                infoEntry ??= entry;
                continue;
            }

            if (!IsPicture(entry.Key))
            {
                continue;
            }

            total += entry.Size;
            if (entries.Count >= settings.MaxEntries
                || entry.Size > settings.MaxPageMegabytes * 1024L * 1024L
                || total > settings.MaxUnpackedMegabytes * 1024L * 1024L)
            {
                throw new ComicPageException(ComicPageFailure.Refused, "This file is too large to open.");
            }

            entries.Add(entry);
        }

        EnsureNotEmpty(entries);
        var info = infoEntry is null ? ComicInfo.Empty : ReadInfo(infoEntry.OpenEntryStream());
        var ordered = entries.OrderBy(e => e.Key!, NaturalComparer.Instance).ToList();

        Directory.CreateDirectory(directory);
        try
        {
            var pages = new List<PageEntry>(ordered.Count);
            long written = 0;
            for (var i = 0; i < ordered.Count; i++)
            {
                var extension = Path.GetExtension(ordered[i].Key!).ToLowerInvariant();
                var name = $"{i + 1:D5}{extension}";
                var target = Path.Combine(directory, name);
                using (var input = ordered[i].OpenEntryStream())
                using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    written += CopyBounded(input, output, settings.MaxPageMegabytes * 1024L * 1024L);
                }

                if (written > settings.MaxUnpackedMegabytes * 1024L * 1024L)
                {
                    throw new ComicPageException(ComicPageFailure.Refused, "This file is too large to open.");
                }

                var (width, height) = ReadSize(() => File.OpenRead(target), settings);
                pages.Add(new PageEntry(name, extension, width, height));
            }

            pages = FrontCoverFirst(pages, info);
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(new UnpackedManifest
            {
                RightToLeft = info.RightToLeft,
                Pages = pages.Select(p => new UnpackedPage { Name = p.Name, Extension = p.Extension, Width = p.Width, Height = p.Height }).ToList(),
            }));
            EvictOldest(keep: directory, settings);
            return new ComicSource(file.FullName, pages, info.RightToLeft, directory);
        }
        catch
        {
            TryDelete(directory);
            throw;
        }
    }

    private static void EnsureNotEmpty<T>(List<T> candidates)
    {
        if (candidates.Count == 0)
        {
            throw new ComicPageException(ComicPageFailure.Unreadable, "There are no pages in this file.");
        }
    }

    private static List<PageEntry> FrontCoverFirst(List<PageEntry> pages, ComicInfo info)
    {
        // The information file's page indexes count in file order, from 0 (D4).
        if (info.FrontCoverIndex is { } index && index > 0 && index < pages.Count)
        {
            var cover = pages[index];
            pages.RemoveAt(index);
            pages.Insert(0, cover);
        }

        return pages;
    }

    // ── Picture work ─────────────────────────────────────────────────────

    private static (int Width, int Height) ReadSize(Func<Stream> open, ComicReaderConfiguration settings)
    {
        try
        {
            using var stream = open();
            var buffer = new byte[SniffBytes];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = stream.Read(buffer, read, buffer.Length - read);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            using var data = SKData.CreateCopy(buffer.AsSpan(0, read));
            using var codec = SKCodec.Create(data);
            if (codec is null)
            {
                return (0, 0);
            }

            var info = codec.Info;
            if ((long)info.Width * info.Height > settings.MaxPageMegapixels * 1_000_000L)
            {
                throw new ComicPageException(ComicPageFailure.Refused, "This file is too large to open.");
            }

            return (info.Width, info.Height);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            return (0, 0); // a single damaged page shows "This page can't be shown" later; the rest of the comic still opens
        }
    }

    private static ComicPageImage MakePicture(byte[] bytes, string extension, int width, ComicReaderConfiguration settings)
    {
        using var bitmap = SKBitmap.Decode(bytes);
        if (bitmap is null)
        {
            throw new ComicPageException(ComicPageFailure.Unreadable, "This page can't be shown.");
        }

        if ((long)bitmap.Width * bitmap.Height > settings.MaxPageMegapixels * 1_000_000L)
        {
            throw new ComicPageException(ComicPageFailure.Refused, "This page is too large to show.");
        }

        if (width == 0 || bitmap.Width <= width)
        {
            return new ComicPageImage(bytes, ContentTypeFor(extension));
        }

        var height = Math.Max(1, (int)Math.Round(bitmap.Height * (width / (double)bitmap.Width)));
        using var resized = bitmap.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        if (resized is null)
        {
            throw new ComicPageException(ComicPageFailure.Unreadable, "This page can't be shown.");
        }

        using var image = SKImage.FromBitmap(resized);
        var keepsTransparency = bitmap.AlphaType != SKAlphaType.Opaque && extension.Equals(".png", StringComparison.OrdinalIgnoreCase);
        using var encoded = keepsTransparency
            ? image.Encode(SKEncodedImageFormat.Png, 100)
            : image.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(settings.JpegQuality, 40, 100));
        return new ComicPageImage(encoded.ToArray(), keepsTransparency ? "image/png" : "image/jpeg");
    }

    private static string ContentTypeFor(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        _ => "image/jpeg",
    };

    // ── Names ────────────────────────────────────────────────────────────

    private static bool IsComicInfo(string name) =>
        Path.GetFileName(name.Replace('\\', '/')).Equals("ComicInfo.xml", StringComparison.OrdinalIgnoreCase);

    private static bool IsPicture(string name)
    {
        var normalized = name.Replace('\\', '/');
        var file = Path.GetFileName(normalized);
        if (string.IsNullOrEmpty(file) || file.StartsWith('.') || !PictureExtensions.Contains(Path.GetExtension(file)))
        {
            return false;
        }

        // System leftovers: macOS resource folders and similar are never pages (D4).
        return !normalized.Split('/').Any(part => part.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase) || part.StartsWith("._", StringComparison.Ordinal));
    }

    private static ComicInfo ReadInfo(Stream stream)
    {
        using (stream)
        {
            try
            {
                using var limited = new MemoryStream();
                CopyBounded(stream, limited, MaxInfoFileBytes);
                limited.Position = 0;
                var document = XDocument.Load(XmlReader.Create(limited, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }));
                var root = document.Root;
                if (root is null)
                {
                    return ComicInfo.Empty;
                }

                var manga = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Manga")?.Value?.Trim();
                var rightToLeft = string.Equals(manga, "YesAndRightToLeft", StringComparison.OrdinalIgnoreCase);
                int? cover = null;
                foreach (var page in root.Descendants().Where(e => e.Name.LocalName == "Page"))
                {
                    if (string.Equals(page.Attribute("Type")?.Value, "FrontCover", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(page.Attribute("Image")?.Value, out var imageIndex) && imageIndex >= 0)
                    {
                        cover = imageIndex;
                        break;
                    }
                }

                return new ComicInfo(rightToLeft, cover);
            }
            catch (Exception ex) when (ex is XmlException or IOException or InvalidDataException or ComicPageException)
            {
                return ComicInfo.Empty; // an unreadable information file never stops the comic opening
            }
        }
    }

    private static long CopyBounded(Stream input, Stream output, long limit)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > limit)
            {
                throw new ComicPageException(ComicPageFailure.Refused, "This file is too large to open.");
            }

            output.Write(buffer, 0, read);
        }

        return total;
    }

    // ── Memory and disk housekeeping ─────────────────────────────────────

    private async Task TrimRememberedAsync(string current)
    {
        if (_sources.Count <= MaxRememberedComics)
        {
            return;
        }

        await Task.Yield();
        foreach (var stale in _lastUsed.Where(p => p.Key != current).OrderBy(p => p.Value).Take(_sources.Count - MaxRememberedComics))
        {
            _sources.TryRemove(stale.Key, out _);
            _lastUsed.TryRemove(stale.Key, out _);
        }
    }

    private void EvictOldest(string keep, ComicReaderConfiguration settings)
    {
        var limit = settings.CacheSizeMb * 1024L * 1024L;
        _evictionGate.Wait();
        try
        {
            var folders = new DirectoryInfo(_cacheRoot).EnumerateDirectories()
                .Select(d => (Folder: d, Size: d.EnumerateFiles().Sum(f => f.Length)))
                .OrderBy(x => x.Folder.LastWriteTimeUtc)
                .ToList();
            var total = folders.Sum(x => x.Size);
            foreach (var (folder, size) in folders)
            {
                if (total <= limit)
                {
                    break;
                }

                if (string.Equals(folder.FullName, keep, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                TryDelete(folder.FullName);
                total -= size;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Clearing the cache is best effort; the next comic opened tries again.
            System.Diagnostics.Trace.TraceWarning($"Comic page cache could not be trimmed ({ex.GetType().Name}).");
        }
        finally
        {
            _evictionGate.Release();
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning($"Comic page cache folder could not be removed ({ex.GetType().Name}).");
        }
    }

    // ── Shapes ───────────────────────────────────────────────────────────

    private sealed record PageEntry(string Name, string Extension, int Width, int Height);

    private sealed record ComicInfo(bool RightToLeft, int? FrontCoverIndex)
    {
        public static ComicInfo Empty { get; } = new(false, null);
    }

    private sealed class UnpackedManifest
    {
        public bool RightToLeft { get; set; }
        public List<UnpackedPage> Pages { get; set; } = [];
    }

    private sealed class UnpackedPage
    {
        public string Name { get; set; } = string.Empty;
        public string Extension { get; set; } = string.Empty;
        public int Width { get; set; }
        public int Height { get; set; }
    }

    private sealed class ComicSource
    {
        private readonly string _filePath;
        private readonly string? _directory;

        public ComicSource(string filePath, List<PageEntry> pages, bool rightToLeft, string? directory)
        {
            _filePath = filePath;
            Pages = pages;
            RightToLeft = rightToLeft;
            _directory = directory;
        }

        public IReadOnlyList<PageEntry> Pages { get; }
        public bool RightToLeft { get; }

        public byte[] ReadPage(PageEntry entry, ComicReaderConfiguration settings)
        {
            var limit = settings.MaxPageMegabytes * 1024L * 1024L;
            try
            {
                using var output = new MemoryStream();
                if (_directory is not null)
                {
                    using var file = File.OpenRead(Path.Combine(_directory, entry.Name));
                    CopyBounded(file, output, limit);
                }
                else
                {
                    using var zip = ZipFile.OpenRead(_filePath);
                    var zipEntry = zip.GetEntry(entry.Name) ?? throw new ComicPageException(ComicPageFailure.Unreadable, "This page can't be shown.");
                    using var input = zipEntry.Open();
                    CopyBounded(input, output, limit);
                }

                return output.ToArray();
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                throw new ComicPageException(ComicPageFailure.Unreadable, "This page can't be shown.");
            }
        }
    }

    /// <summary>Natural order: "page 2" comes before "page 10" (D4).</summary>
    private sealed class NaturalComparer : IComparer<string>
    {
        public static NaturalComparer Instance { get; } = new();

        public int Compare(string? x, string? y)
        {
            x = (x ?? string.Empty).Replace('\\', '/');
            y = (y ?? string.Empty).Replace('\\', '/');
            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
                {
                    var si = i;
                    var sj = j;
                    while (i < x.Length && char.IsDigit(x[i])) { i++; }
                    while (j < y.Length && char.IsDigit(y[j])) { j++; }
                    var a = x[si..i].TrimStart('0');
                    var b = y[sj..j].TrimStart('0');
                    if (a.Length != b.Length)
                    {
                        return a.Length.CompareTo(b.Length);
                    }

                    var compared = string.CompareOrdinal(a, b);
                    if (compared != 0)
                    {
                        return compared;
                    }
                }
                else
                {
                    var compared = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                    if (compared != 0)
                    {
                        return compared;
                    }

                    i++;
                    j++;
                }
            }

            return (x.Length - i).CompareTo(y.Length - j);
        }
    }
}
