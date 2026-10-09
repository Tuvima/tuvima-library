using MediaEngine.Api.Endpoints;
using MediaEngine.Domain.Configuration;
using MediaEngine.Ingestion.Models;
using Microsoft.AspNetCore.Http;

namespace MediaEngine.Api.Tests;

public sealed class UploadSafetyTests
{
    private static readonly IReadOnlyList<MediaTypeDefinition> MediaTypes = MediaTypeConfiguration.DefaultTypes();

    [Fact]
    public void UploadEndpoints_KeepViewAndCatalogueIntakeSeparated()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Endpoints/IngestionEndpoints.cs"));
        var view = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Endpoints/ViewEndpoints.cs"));

        Assert.Contains("form[\"destinationLibraryId\"]", source, StringComparison.Ordinal);
        Assert.Contains("library.PrimaryDestination", source, StringComparison.Ordinal);
        Assert.Contains("library.Kind != LibraryKinds.Catalogued", source, StringComparison.Ordinal);
        Assert.Contains("profile-owned View upload endpoint", source, StringComparison.Ordinal);
        Assert.Contains("engine.EnqueueIntakeAsync(new IntakeFileRequest", source, StringComparison.Ordinal);
        Assert.Contains("DestinationLibraryId = library.Id", source, StringComparison.Ordinal);
        Assert.Contains("SourceId = destination.Id", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ViewLibraryService", source, StringComparison.Ordinal);
        Assert.Contains("service.UploadAsync", view, StringComparison.Ordinal);
        Assert.Contains("EnsurePersonalSpaceAsync", view, StringComparison.Ordinal);
        Assert.Contains("UploadSafety.FinalizeUploadAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FinalizeUpload_RemovesCompletedFileWhenLocalIndexingFails()
    {
        using var temp = new TempDirectory();
        var staging = System.IO.Path.Combine(temp.Path, ".photo.uploading");
        var target = System.IO.Path.Combine(temp.Path, "photo.jpg");
        await File.WriteAllTextAsync(staging, "original");

        await Assert.ThrowsAsync<InvalidDataException>(() => UploadSafety.FinalizeUploadAsync(
            staging,
            target,
            (_, _) => throw new InvalidDataException("Local indexing failed.")));

        Assert.False(File.Exists(staging));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void CreatePlan_RejectsUnsupportedMediaType()
    {
        using var temp = new TempDirectory();

        var plan = UploadSafety.CreatePlan(
            temp.Path,
            "Documents",
            "notes.epub",
            1024,
            MediaTypes,
            new IngestionOptions());

        Assert.False(plan.IsValid);
        Assert.Equal(StatusCodes.Status400BadRequest, ExecuteStatusCode(plan.Error!));
    }

    [Fact]
    public void CreatePlan_RejectsUnsupportedExtension()
    {
        using var temp = new TempDirectory();

        var plan = UploadSafety.CreatePlan(
            temp.Path,
            "Books",
            "notes.exe",
            1024,
            MediaTypes,
            new IngestionOptions());

        Assert.False(plan.IsValid);
        Assert.Equal(StatusCodes.Status400BadRequest, ExecuteStatusCode(plan.Error!));
    }

    [Fact]
    public void CreatePlan_RejectsOversizedFile()
    {
        using var temp = new TempDirectory();

        var plan = UploadSafety.CreatePlan(
            temp.Path,
            "Movies",
            "movie.mkv",
            1024,
            MediaTypes,
            new IngestionOptions { MaxUploadSizeBytes = 512 });

        Assert.False(plan.IsValid);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, ExecuteStatusCode(plan.Error!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../book.epub")]
    [InlineData("folder/book.epub")]
    public void CreatePlan_RejectsBadFilename(string fileName)
    {
        using var temp = new TempDirectory();

        var plan = UploadSafety.CreatePlan(
            temp.Path,
            "Books",
            fileName,
            1024,
            MediaTypes,
            new IngestionOptions());

        Assert.False(plan.IsValid);
        Assert.Equal(StatusCodes.Status400BadRequest, ExecuteStatusCode(plan.Error!));
    }

    [Fact]
    public void CreatePlan_AcceptsAllowedFileUsingTempDirectory()
    {
        using var temp = new TempDirectory();

        var plan = UploadSafety.CreatePlan(
            temp.Path,
            "Books",
            "book.epub",
            1024,
            MediaTypes,
            new IngestionOptions());

        Assert.True(plan.IsValid);
        Assert.Equal("Books", plan.CanonicalMediaType);
        Assert.EndsWith(Path.Combine("Books", "book.epub"), plan.TargetPath);
    }

    [Fact]
    public void CreateDestinationPlan_TargetsExplicitLibraryRootWithoutCategoryRediscovery()
    {
        using var temp = new TempDirectory();

        var plan = UploadSafety.CreateDestinationPlan(
            temp.Path,
            "Books",
            "book.epub",
            1024,
            MediaTypes,
            new IngestionOptions(),
            allowPersonalFiles: false);

        Assert.True(plan.IsValid);
        Assert.Equal(Path.Combine(temp.Path, "book.epub"), plan.TargetPath);
    }

    [Theory]
    [InlineData("photo.heic")]
    [InlineData("clip.mov")]
    [InlineData("plan.docx")]
    [InlineData("note.m4a")]
    public void CreateDestinationPlan_AcceptsMixedPersonalLibraryFiles(string fileName)
    {
        using var temp = new TempDirectory();

        var plan = UploadSafety.CreateDestinationPlan(
            temp.Path,
            null,
            fileName,
            1024,
            MediaTypes,
            new IngestionOptions(),
            allowPersonalFiles: true);

        Assert.True(plan.IsValid);
        Assert.Equal("Personal", plan.CanonicalMediaType);
    }

    [Fact]
    public void CreateDestinationPlan_RejectsExecutableForPersonalLibrary()
    {
        using var temp = new TempDirectory();

        var plan = UploadSafety.CreateDestinationPlan(
            temp.Path,
            null,
            "payload.exe",
            1024,
            MediaTypes,
            new IngestionOptions(),
            allowPersonalFiles: true);

        Assert.False(plan.IsValid);
        Assert.Equal(StatusCodes.Status400BadRequest, ExecuteStatusCode(plan.Error!));
    }

    private static int? ExecuteStatusCode(IResult result)
    {
        var statusCodeProperty = result.GetType().GetProperty("StatusCode");
        return statusCodeProperty?.GetValue(result) as int?;
    }

    private static string GetRepoFilePath(string relativePath) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath));

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tuvima-upload-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
