using System.Text.Json;
using MediaEngine.Domain.Configuration;
using MediaEngine.Ingestion.Services;
using MediaEngine.Storage.Configuration;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Ingestion.Tests;

/// <summary>
/// A libraries file whose own settings disagree with a library's kind still loads: the kind wins, one warning names the library and what
/// was overridden, and the file never stops the Engine. Only a person's save is refused for it (Library setup D38).
/// </summary>
public sealed class LibraryKindLoadingTests
{
    private static LibrariesConfiguration ShippedWithOtherVideosSaying(string metadataPolicy, bool youtubeLookup)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "MediaEngine.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        var config = JsonSerializer.Deserialize<LibrariesConfiguration>(File.ReadAllText(Path.Combine(dir!, "config", "libraries.json")))!;
        var videos = config.Libraries.Single(library => library.KindKey == "other_videos");
        videos.Id = Guid.NewGuid().ToString("D");
        videos.MetadataPolicy = metadataPolicy;
        videos.YoutubeLookup = youtubeLookup;
        return config;
    }

    [Fact]
    public void AFileThatContradictsItsKindLoads_TheKindWins_AndOneWarningNamesTheLibraryAndWhatWasOverridden()
    {
        var config = ShippedWithOtherVideosSaying(LibraryMetadataPolicies.Enriched, youtubeLookup: true);
        var log = new CapturingLogger();

        Assert.DoesNotContain(JsonConfigValidator.Validate(config, "libraries.json", LibraryKindsConfiguration.BuiltIn()), error => !error.Contains("absolute path"));
        var entries = LibraryFolderEntryFactory.CreateAll(config.Libraries, LibraryKindsConfiguration.BuiltIn(), log);

        var videos = entries.Single(entry => entry.KindKey == "other_videos");
        Assert.False(videos.AsksYoutube);
        Assert.False(videos.YoutubeLookup);
        Assert.True(videos.BypassesExternalIdentity);
        Assert.Equal(LibraryMetadataPolicies.LocalOnly, videos.MetadataPolicy);
        var warning = Assert.Single(log.Warnings);
        Assert.Contains("The library \"Videos\"", warning, StringComparison.Ordinal);
        Assert.Contains("metadata_policy is \"enriched\"", warning, StringComparison.Ordinal);
        Assert.Contains("youtube_lookup is true", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadingTheSameFileAgain_DoesNotRepeatTheWarning()
    {
        var config = ShippedWithOtherVideosSaying(LibraryMetadataPolicies.LocalOnly, youtubeLookup: true);
        var log = new CapturingLogger();

        LibraryFolderEntryFactory.CreateAll(config.Libraries, LibraryKindsConfiguration.BuiltIn(), log);
        LibraryFolderEntryFactory.CreateAll(config.Libraries, LibraryKindsConfiguration.BuiltIn(), log);

        Assert.Single(log.Warnings);
    }

    [Fact]
    public void ALibraryThatAgreesWithItsKind_LogsNothing()
    {
        var config = ShippedWithOtherVideosSaying(LibraryMetadataPolicies.LocalOnly, youtubeLookup: false);
        var log = new CapturingLogger();

        LibraryFolderEntryFactory.CreateAll(config.Libraries, LibraryKindsConfiguration.BuiltIn(), log);

        Assert.Empty(log.Warnings);
    }

    [Fact]
    public void ASaveThatContradictsItsKindIsNotRefused_TheKindsValuesAreWrittenInstead()
    {
        var config = ShippedWithOtherVideosSaying(LibraryMetadataPolicies.Enriched, youtubeLookup: true);

        Assert.Empty(LibraryKindRules.LibraryProblems(config, LibraryKindsConfiguration.BuiltIn()));
        LibraryKindLookupPolicy.Normalise(config, LibraryKindsConfiguration.BuiltIn());

        var videos = config.Libraries.Single(library => library.KindKey == "other_videos");
        Assert.Equal(LibraryMetadataPolicies.LocalOnly, videos.MetadataPolicy);
        Assert.False(videos.YoutubeLookup);
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
