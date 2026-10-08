---
title: "How to Write a New File Format Processor"
description: "Add support for a new media format and connect it to ingestion, metadata extraction, and tests."
audience: "developer"
category: "guide"
product_area: "processors"
tags:
  - "processors"
  - "formats"
  - "ingestion"
status: current
---

# How to Write a New File Format Processor

## In this page

Add a file-format reader that identifies bytes and emits metadata without changing source media. Follow the existing processor contract, shared registration, and focused verification steps.

## Where this lives in the code

- `src/MediaEngine.Processors/Contracts/IMediaProcessor.cs`
- `src/MediaEngine.Processors/Models/ProcessorResult.cs`
- `src/MediaEngine.Ingestion/DependencyInjection/MediaEngineIngestionServiceCollectionExtensions.cs`

This guide explains how to add support for a new file format to the Tuvima Library
ingestion pipeline by implementing `IMediaProcessor`.

---

## Prerequisites

- Familiarity with the ingestion pipeline (`docs/architecture/ingestion-pipeline.md`)
- Knowledge of the target file format's binary structure (magic bytes)
- A NuGet package or library for reading the format, if applicable

---

## Overview of the processor system

When the ingestion engine encounters a file, it calls `MediaProcessorRouter.ProcessAsync`.
The router iterates all registered processors in descending `Priority` order and asks
each one `CanProcess(filePath)`. The first processor to return `true` wins. If none match,
the `GenericFileProcessor` (priority `int.MinValue`) handles the file as a fallback.

Current processors and their priorities:

| Processor | Priority | Media type | Format(s) |
|---|---|---|---|
| `EpubProcessor` | 100 | Books | EPUB 2/3 |
| `AzW3Processor` | 99 | Books | AZW3 |
| `PdfProcessor` | 98 | Books | PDF |
| `DiscImageProcessor` | 97 | Movies | Supported disc images |
| `AudioProcessor` | 95 | Audiobooks/Music | MP3, M4A, M4B, FLAC, OGG, WAV |
| `VideoProcessor` | 90 | Movies/TV | MP4, MKV, AVI, etc. |
| `ComicProcessor` | 85 | Comics | CBZ, CBR |
| `GenericFileProcessor` | `int.MinValue` | Unknown | Any |

---

## The `IMediaProcessor` interface

Located in `src/MediaEngine.Processors/Contracts/IMediaProcessor.cs`:

```csharp
public interface IMediaProcessor
{
    MediaType SupportedType { get; }
    int Priority { get; }
    bool CanProcess(string filePath);
    Task<ProcessorResult> ProcessAsync(string filePath, CancellationToken ct = default);
}
```

### Contract rules (read before implementing)

- **`CanProcess` must use magic bytes, not file extension.** Extensions are unreliable
  on user-managed media collections. Read the minimum number of bytes required
  (typically 4-16).
- **Implementations must be stateless.** No instance fields that vary between calls.
  The same instance is called concurrently from multiple threads.
- **Never modify, move, or delete the source file.** The processor is read-only.
- **Return `IsCorrupt = true` instead of throwing** when a file is malformed. The
  ingestion engine quarantines corrupt files rather than crashing.
- **`ProcessAsync` is safe to call without a prior `CanProcess`.** Validate the magic
  bytes again at the start of `ProcessAsync` so the method works correctly in isolation.

---

## `ProcessorResult` - what to return

Located in `src/MediaEngine.Processors/Models/ProcessorResult.cs`:

```csharp
public sealed class ProcessorResult
{
    public required string FilePath { get; init; }
    public required MediaType DetectedType { get; init; }
    public IReadOnlyList<ExtractedClaim> Claims { get; init; } = [];
    public byte[]? CoverImage { get; init; }
    public string? CoverImageMimeType { get; init; }
    public bool IsCorrupt { get; init; }
    public string? CorruptReason { get; init; }
    public IReadOnlyList<MediaTypeCandidate> MediaTypeCandidates { get; init; } = [];
}
```

`Claims` is a list of `ExtractedClaim { string Key, string Value, double Confidence }`.
Claim keys must match the constants in `MediaEngine.Domain.MetadataFieldConstants`
(e.g. `"title"`, `"author"`, `"year"`, `"isbn"`, `"publisher"`, `"language"`, `"description"`).

### Confidence conventions

The ingestion engine feeds claims into the Priority Cascade against claims from external
providers. Use these conventions so competing claims resolve correctly:

| Source | Confidence | Examples |
|---|---|---|
| Embedded structured metadata | 0.90-0.95 | OPF `<dc:title>`, ID3 TPE1 tag |
| Embedded but less reliable | 0.75-0.85 | Comment tags, embedded descriptions |
| Heuristic derivation | 0.50-0.70 | Filename stem as title fallback |
| Definitive embedded identifier | 1.00 | ISBN in `<dc:identifier scheme="ISBN">` |

Do not use `1.0` for anything other than definitive embedded identifiers (ISBN, ASIN,
IMDB ID). A confidence of `1.0` remains extraction evidence; it does not create a user lock. The Priority Cascade honors explicit locks only for its supported lockable fields.

### Cover image extraction

If the format embeds cover art, return it in `CoverImage` as raw bytes with the IANA
MIME type in `CoverImageMimeType`. Sniff the MIME type from the leading bytes rather than
trusting any embedded label:

```csharp
private static string SniffMimeType(byte[] data)
{
    if (data.Length < 4) return "image/jpeg";
    if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return "image/jpeg";
    if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47) return "image/png";
    if (data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x38) return "image/gif";
    return "image/jpeg"; // safe default
}
```

The ingestion engine hands cover bytes to the managed asset pipeline. Accepted
artwork is stored under `.data/assets` and indexed through `entity_assets` with
generated renditions. You do not need to resize the image.

### Ambiguous formats - `MediaTypeCandidates`

Some containers (MP3, M4A, MP4) can hold different media types (music vs audiobook,
movie vs TV episode). When the processor cannot determine the media type from the
container alone:

1. Set `DetectedType` to the most likely type for callers that need one summary value.
2. Populate `MediaTypeCandidates` with all plausible types and heuristic confidences.
3. The ingestion engine passes the candidates to the AI `MediaTypeAdvisor`, which
   classifies the file using metadata signals (album field, track number, episode
   patterns in the filename, narrator tags, etc.).

```csharp
var candidates = new List<MediaTypeCandidate>
{
    new() { MediaType = MediaType.Music,      Confidence = 0.6, Reason = "No chapter markers" },
    new() { MediaType = MediaType.Audiobooks, Confidence = 0.4, Reason = "Single-track MP3" },
};

return new ProcessorResult
{
    FilePath             = filePath,
    DetectedType         = MediaType.Music,  // top candidate
    Claims               = claims,
    MediaTypeCandidates  = candidates,
};
```

Leave `MediaTypeCandidates` empty for unambiguous formats (EPUB, FLAC, CBZ, M4B).

---

## Step-by-step: implementing a new processor

### 1. Create the file

Add your processor in `src/MediaEngine.Processors/Processors/`. Follow the naming
convention: `{FormatName}Processor.cs`.

### 2. Determine the magic bytes

Every well-formed binary format has a recognisable byte sequence at the start of the
file. Find it in the format specification or by inspecting real files with a hex editor.

Example magic bytes for common formats:

| Format | Offset | Bytes | ASCII hint |
|---|---|---|---|
| ZIP (EPUB, CBZ) | 0 | `50 4B 03 04` | `PK\x03\x04` |
| JPEG | 0 | `FF D8 FF` | - |
| PNG | 0 | `89 50 4E 47` | `\x89PNG` |
| PDF | 0 | `25 50 44 46` | `%PDF` |
| FLAC | 0 | `66 4C 61 43` | `fLaC` |
| MP3 (ID3v2) | 0 | `49 44 33` | `ID3` |
| ISO BMFF (MP4, M4A, M4B) | 4 | `66 74 79 70` | `ftyp` |
| OGG | 0 | `4F 67 67 53` | `OggS` |

Read bytes using a minimal `FileStream` with `FileShare.Read` to avoid blocking other
processes that might be writing to the file:

```csharp
private static bool HasMagicBytes(string filePath)
{
    try
    {
        Span<byte> header = stackalloc byte[4];
        using var fs = new FileStream(
            filePath, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 4, FileOptions.None);
        int read = fs.Read(header);
        return read == 4 && header[0] == 0x4D && header[1] == 0x4D; // your check
    }
    catch (IOException) { return false; }
    catch (UnauthorizedAccessException) { return false; }
}
```

### 3. Write the skeleton

This is a format-adaptation sketch, not a complete parser: replace `MyFormatReader`, `MyFormatDocument`, and the sample magic bytes with your real parser. Those parser types are placeholders, not shipped Tuvima interfaces.

```csharp
using MediaEngine.Domain.Enums;
using MediaEngine.Processors.Contracts;
using MediaEngine.Processors.Models;

namespace MediaEngine.Processors.Processors;

/// <summary>
/// Identifies and extracts metadata from MyFormat files.
/// Detection: sample magic bytes 4D 4D 4D 4D at offset 0.
/// </summary>
public sealed class MyFormatProcessor : IMediaProcessor
{
    private static ReadOnlySpan<byte> Magic => [0x4D, 0x4D, 0x4D, 0x4D];

    public MediaType SupportedType => MediaType.Books; // choose the appropriate type

    /// <remarks>
    /// Priority N - explain why this value was chosen relative to existing processors.
    /// </remarks>
    public int Priority => 88;  // between Comic (85) and Video (90) for example

    public bool CanProcess(string filePath)
    {
        if (!File.Exists(filePath)) return false;
        return HasMagicBytes(filePath);
    }

    public async Task<ProcessorResult> ProcessAsync(
        string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!HasMagicBytes(filePath))
            return Corrupt(filePath, "File does not begin with expected magic bytes.");

        // Parse the file using your chosen library.
        MyFormatDocument doc;
        try
        {
            doc = await MyFormatReader.ReadAsync(filePath, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Corrupt(filePath, $"Failed to parse: {ex.Message}");
        }

        ct.ThrowIfCancellationRequested();

        var claims = BuildClaims(doc);
        var (coverBytes, coverMime) = ExtractCover(doc);

        return new ProcessorResult
        {
            FilePath           = filePath,
            DetectedType       = SupportedType,
            Claims             = claims,
            CoverImage         = coverBytes,
            CoverImageMimeType = coverMime,
        };
    }

    // -- Magic bytes --------------------------------------------------------

    private static bool HasMagicBytes(string filePath)
    {
        try
        {
            Span<byte> header = stackalloc byte[4];
            using var fs = new FileStream(
                filePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 4, FileOptions.None);
            int read = fs.Read(header);
            return read == 4 && header.SequenceEqual(Magic);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    // -- Metadata extraction -----------------------------------------------

    private static List<ExtractedClaim> BuildClaims(MyFormatDocument doc)
    {
        var claims = new List<ExtractedClaim>();

        if (!string.IsNullOrWhiteSpace(doc.Title))
            claims.Add(new ExtractedClaim { Key = "title", Value = doc.Title.Trim(), Confidence = 0.90 });

        if (!string.IsNullOrWhiteSpace(doc.Author))
            claims.Add(new ExtractedClaim { Key = "author", Value = doc.Author.Trim(), Confidence = 0.90 });

        if (!string.IsNullOrWhiteSpace(doc.Year))
            claims.Add(new ExtractedClaim { Key = "year", Value = doc.Year, Confidence = 0.85 });

        return claims;
    }

    private static (byte[]? bytes, string? mime) ExtractCover(MyFormatDocument doc)
    {
        if (doc.CoverImage is null or { Length: 0 }) return (null, null);
        return (doc.CoverImage, SniffMimeType(doc.CoverImage));
    }

    private static string SniffMimeType(byte[] data)
    {
        if (data.Length < 4) return "image/jpeg";
        if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return "image/jpeg";
        if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47) return "image/png";
        return "image/jpeg";
    }

    // -- Factory helpers ----------------------------------------------------

    private static ProcessorResult Corrupt(string filePath, string reason) => new()
    {
        FilePath      = filePath,
        DetectedType  = MediaType.Unknown,
        IsCorrupt     = true,
        CorruptReason = reason,
    };
}
```

### 4. Choose the right priority

Pick a priority that places your processor logically in the evaluation order:

- Higher than any processor it should shadow (e.g. if your format is ZIP-based
  like CBZ, place it above `GenericFileProcessor` but below `EpubProcessor` if
  EPUB detection should win first).
- Lower than formats with more specific or expensive detection if your format
  detection is fast and broad.

Add a `<remarks>` comment explaining the priority relative to adjacent processors.

### 5. Register the processor in DI

Open `src/MediaEngine.Ingestion/DependencyInjection/MediaEngineIngestionServiceCollectionExtensions.cs` and find the `IProcessorRouter` registration block shared by the Engine and ingestion host. Insert your processor while preserving the existing registrations. The example below shows the relevant shape:

```csharp
services.AddSingleton<IProcessorRouter>(sp =>
{
    var router = new MediaProcessorRouter();
    router.Register(new EpubProcessor());
    router.Register(new AzW3Processor());
    router.Register(new PdfProcessor());
    router.Register(new DiscImageProcessor());
    router.Register(new AudioProcessor());
    router.Register(new VideoProcessor(sp.GetRequiredService<IVideoMetadataExtractor>()));
    router.Register(new ComicProcessor());
    // Add your processor here:
    router.Register(new MyFormatProcessor());
    router.Register(new GenericFileProcessor(sp.GetRequiredService<IMediaTypeExtensionCatalog>()));
    return router;
});
```

If your processor depends on a service from DI, resolve it via `sp.GetRequiredService<T>()`.

### 6. Handle format-specific edge cases

**Multi-file formats:** Some formats use companion sidecar files (`.nfo`, `.opf`). Read
only the primary binary file in `CanProcess` and `ProcessAsync`. Supplementary metadata
is handled by the ingestion pipeline's sidecar reader, not the processor.

**Very large files:** Read metadata from the beginning of the file when possible, not
by loading the whole file into memory. Most formats embed metadata in a header or
beginning section. Use streaming APIs (`FileStream`, `ZipArchive` with lazy entry
opening) rather than `File.ReadAllBytes`.

**Format variants:** If a container format has multiple sub-types (e.g. M4A vs M4B
audio, both ISO BMFF), detect the sub-type inside `ProcessAsync` after reading the
ftyp box or equivalent, and set `DetectedType` accordingly.

---

## Writing unit tests

Add a test class in `tests/MediaEngine.Processors.Tests/`. Tests should cover:

1. `CanProcess` returns `true` for valid sample files of the target format.
2. `CanProcess` returns `false` for files of other formats (EPUB, MP3, a `.txt` file).
3. `ProcessAsync` extracts the expected claims from a known sample file.
4. `ProcessAsync` returns `IsCorrupt = true` for a truncated or malformed file.
5. `ProcessAsync` returns a non-null `CoverImage` when the sample contains embedded art.

```csharp
public sealed class MyFormatProcessorTests
{
    private static readonly string SamplesDir =
        Path.Combine(AppContext.BaseDirectory, "Samples", "MyFormat");

    private readonly MyFormatProcessor _sut = new();

    [Fact]
    public void CanProcess_ReturnsTrue_ForValidFile()
    {
        var path = Path.Combine(SamplesDir, "sample.myformat");
        Assert.True(_sut.CanProcess(path));
    }

    [Fact]
    public void CanProcess_ReturnsFalse_ForEpub()
    {
        var path = Path.Combine(SamplesDir, "..", "Epub", "sample.epub");
        Assert.False(_sut.CanProcess(path));
    }

    [Fact]
    public async Task ProcessAsync_ExtractsTitleAndAuthor()
    {
        var path = Path.Combine(SamplesDir, "sample.myformat");
        var result = await _sut.ProcessAsync(path);

        Assert.False(result.IsCorrupt);
        Assert.Contains(result.Claims, c => c.Key == "title" && !string.IsNullOrEmpty(c.Value));
        Assert.Contains(result.Claims, c => c.Key == "author" && !string.IsNullOrEmpty(c.Value));
    }

    [Fact]
    public async Task ProcessAsync_ReturnsCorrupt_ForTruncatedFile()
    {
        var path = Path.Combine(SamplesDir, "truncated.myformat");
        var result = await _sut.ProcessAsync(path);

        Assert.True(result.IsCorrupt);
        Assert.NotNull(result.CorruptReason);
    }
}
```

Place sample files in `tests/MediaEngine.Processors.Tests/Samples/MyFormat/`.
Set their build action to `Content` with `Copy if newer` in the `.csproj`:

```xml
<ItemGroup>
  <Content Include="Samples\**\*" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

Run tests with `dotnet test tests/MediaEngine.Processors.Tests`.

---

## Adding a NuGet dependency

If parsing the format requires a library, verify its license is compatible with AGPLv3
(check the exact package license and distribution obligations) before adding it.

Add the package reference to `src/MediaEngine.Processors/MediaEngine.Processors.csproj`:

```xml
<PackageReference Include="YourParser.Library" />
```

Declare the version centrally in `Directory.Packages.props` with `<PackageVersion Include="YourParser.Library" Version="X.Y.Z" />`. Record attribution and retain required license notices in the owning documentation.

---

## Checklist before committing

- [ ] `CanProcess` uses magic bytes, not file extension
- [ ] `CanProcess` never throws - catches `IOException` and `UnauthorizedAccessException`
- [ ] `ProcessAsync` re-validates magic bytes before parsing
- [ ] `ProcessAsync` returns `IsCorrupt = true` on all parse failures instead of throwing
- [ ] `Priority` comment explains the value relative to adjacent processors
- [ ] Processor registered in the shared ingestion DI module before `GenericFileProcessor`
- [ ] Unit tests cover happy path, wrong format rejection, corrupt file
- [ ] `dotnet build` passes with 0 errors, 0 warnings
- [ ] License of any new NuGet dependency verified and attribution recorded

---

## Related

- [Supported Media Types and Formats](../reference/media-types.md)
- [Ingestion Pipeline](../architecture/ingestion-pipeline.md)
- [Developer Setup](../tutorials/dev-setup.md)
