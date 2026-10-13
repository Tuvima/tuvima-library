using MediaEngine.Domain.Configuration;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Services;

namespace MediaEngine.Ingestion.Tests;

public sealed class SourceMutationPolicyGateTests
{
    private readonly SourceMutationPolicyGate _gate = new();

    [Theory]
    [InlineData(SourceMutationKind.Move)]
    [InlineData(SourceMutationKind.Rename)]
    [InlineData(SourceMutationKind.Delete)]
    [InlineData(SourceMutationKind.MetadataWriteback)]
    public void ManagedFolder_MayChangeFilesThatAreAlreadyThere(SourceMutationKind mutation)
    {
        var root = NewRoot();
        var (library, source) = ManagedLibrary(root);
        var policy = FileSourceMutationPolicyFactory.Create(library, source, allowDelete: true);

        // Existing files are not protected: Tuvima moves, renames and tags what is already in a managed folder.
        Assert.True(_gate.Evaluate(new SourceMutationRequest { Source = policy, Mutation = mutation, Path = Path.Combine(root, "owned.txt") }).Allowed);
        Assert.True(_gate.Evaluate(new SourceMutationRequest { Source = policy, Mutation = SourceMutationKind.UseAsDestination, Path = Path.Combine(root, "new.txt") }).Allowed);
        // A path outside the folder is never reachable through it.
        Assert.False(_gate.Evaluate(new SourceMutationRequest { Source = policy, Mutation = mutation, Path = Path.Combine(Path.GetDirectoryName(root)!, "elsewhere", "owned.txt") }).Allowed);
    }

    [Fact]
    public void ManagedFolder_DeleteNeedsTheCallerToAskForIt()
    {
        var root = NewRoot();
        var (library, source) = ManagedLibrary(root);

        var policy = FileSourceMutationPolicyFactory.Create(library, source);

        Assert.False(policy.AllowDelete);
        Assert.True(FileSourceMutationPolicyFactory.Create(library, source, allowDelete: true).AllowDelete);
    }

    [Fact]
    public void AsIsFolder_DeniesMoveRenameWritebackDeleteAndDestinationUse_EvenWhenEverythingElseSaysYes()
    {
        var root = NewRoot();
        var source = new LibrarySourceConfig
        {
            Id = "source",
            Path = root,
            ManagementMode = LibrarySourceManagementModes.ExistingLibrary,
            AccessMode = LibrarySourceAccessModes.Writable,
        };
        // The destination id points at the as-is folder, the type is set to write tags, delete and merge are requested.
        var library = new LibraryFolderConfig
        {
            Id = "library",
            Sources = [source],
            PrimaryDestinationSourceId = "source",
            OrganizationPolicy = new() { WriteTags = true },
        };

        var policy = FileSourceMutationPolicyFactory.Create(library, source, allowDelete: true, isMergeTarget: true);

        Assert.Null(library.PrimaryDestination);
        Assert.False(policy.AllowMove);
        Assert.False(policy.AllowRename);
        Assert.False(policy.AllowMetadataWriteback);
        Assert.False(policy.AllowDelete);
        Assert.False(policy.AllowAsDestination);
        foreach (var mutation in Enum.GetValues<SourceMutationKind>())
        {
            Assert.False(
                _gate.Evaluate(new SourceMutationRequest { Source = policy, Mutation = mutation, Path = Path.Combine(root, "item.mkv") }).Allowed,
                $"{mutation} must be denied in an as-is folder");
        }
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void TagsAreWrittenOnlyForManagedFoldersWhoseTypeWritesTags(bool managed, bool writeTags, bool expected)
    {
        var root = NewRoot();
        var source = new LibrarySourceConfig
        {
            Id = "source",
            Path = root,
            ManagementMode = managed ? LibrarySourceManagementModes.ManagedByTuvima : LibrarySourceManagementModes.ExistingLibrary,
            AccessMode = managed ? LibrarySourceAccessModes.Writable : LibrarySourceAccessModes.ReadOnly,
        };
        var library = new LibraryFolderConfig
        {
            Id = "library",
            Sources = [source],
            OrganizationPolicy = new() { WriteTags = writeTags },
        };

        var policy = FileSourceMutationPolicyFactory.Create(library, source);
        var decision = _gate.Evaluate(new SourceMutationRequest
        {
            Source = policy,
            Mutation = SourceMutationKind.MetadataWriteback,
            Path = Path.Combine(root, "book.epub"),
        });

        Assert.Equal(expected, decision.Allowed);
        // The format check is separate: an EPUB can carry tags and a plain text file cannot.
        Assert.True(WriteableFormats.CanCarryTags("book.epub"));
        Assert.False(WriteableFormats.CanCarryTags("notes.txt"));
    }

    [Fact]
    public void OnlyTheDestinationOrAMergeTargetMayReceiveFiles()
    {
        var firstRoot = NewRoot();
        var secondRoot = NewRoot();
        var first = new LibrarySourceConfig { Id = "first", Path = firstRoot, ManagementMode = LibrarySourceManagementModes.ManagedByTuvima, AccessMode = LibrarySourceAccessModes.Writable };
        var second = new LibrarySourceConfig { Id = "second", Path = secondRoot, ManagementMode = LibrarySourceManagementModes.ManagedByTuvima, AccessMode = LibrarySourceAccessModes.Writable };
        var library = new LibraryFolderConfig { Id = "library", Sources = [first, second], PrimaryDestinationSourceId = "first" };

        Assert.True(FileSourceMutationPolicyFactory.Create(library, first).AllowAsDestination);
        Assert.False(FileSourceMutationPolicyFactory.Create(library, second).AllowAsDestination);
        Assert.True(FileSourceMutationPolicyFactory.Create(library, second, isMergeTarget: true).AllowAsDestination);
        // The second folder can still be reorganised in place.
        Assert.True(FileSourceMutationPolicyFactory.Create(library, second).AllowMove);
    }

    [Fact]
    public void AConvertingFolderCanStillBeTheDestination()
    {
        var root = NewRoot();
        var (library, source) = ManagedLibrary(root);
        source.ConversionState = LibrarySourceConversionStates.Converting;
        source.ConversionOperationId = Guid.NewGuid().ToString("D");

        Assert.True(FileSourceMutationPolicyFactory.Create(library, source).AllowAsDestination);
    }

    private static (LibraryFolderConfig Library, LibrarySourceConfig Source) ManagedLibrary(string root)
    {
        var source = new LibrarySourceConfig
        {
            Id = "source",
            Path = root,
            ManagementMode = LibrarySourceManagementModes.ManagedByTuvima,
            AccessMode = LibrarySourceAccessModes.Writable,
        };
        var library = new LibraryFolderConfig { Id = "library", Sources = [source], PrimaryDestinationSourceId = "source" };
        return (library, source);
    }

    /// <summary>Tags can be written only to a format a tagger handles. Uses the real taggers' own answer.</summary>
    private static class WriteableFormats
    {
        public static bool CanCarryTags(string path)
        {
            var taggers = new MediaEngine.Ingestion.Contracts.IMetadataTagger[]
            {
                new EpubMetadataTagger(Microsoft.Extensions.Logging.Abstractions.NullLogger<EpubMetadataTagger>.Instance),
            };
            return taggers.Any(tagger => tagger.CanHandle(path));
        }
    }

    [Theory]
    [InlineData(SourceMutationKind.Move)]
    [InlineData(SourceMutationKind.Rename)]
    [InlineData(SourceMutationKind.MetadataWriteback)]
    [InlineData(SourceMutationKind.Delete)]
    [InlineData(SourceMutationKind.UseAsDestination)]
    public void ExistingLibrary_DeniesEveryFilesystemMutation(SourceMutationKind mutation)
    {
        var root = NewRoot();
        var policy = Policy(root, FileSourceManagementMode.ExistingLibrary);

        var result = _gate.Evaluate(new SourceMutationRequest
        {
            Source = policy,
            Mutation = mutation,
            Path = Path.Combine(root, "media", "item.mkv"),
        });

        Assert.False(result.Allowed);
        Assert.Contains("read-only", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ManagedSource_MustBeWritable()
    {
        var root = NewRoot();
        var policy = Policy(root, FileSourceManagementMode.ManagedByTuvima) with
        {
            IsWritable = false,
        };

        var result = _gate.Evaluate(new SourceMutationRequest
        {
            Source = policy,
            Mutation = SourceMutationKind.Move,
            Path = Path.Combine(root, "item.mkv"),
        });

        Assert.False(result.Allowed);
        Assert.Contains("not writable", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ManagedSource_RequiresActionSpecificPermission()
    {
        var root = NewRoot();
        var policy = Policy(root, FileSourceManagementMode.ManagedByTuvima) with
        {
            AllowDelete = false,
        };

        var result = _gate.Evaluate(new SourceMutationRequest
        {
            Source = policy,
            Mutation = SourceMutationKind.Delete,
            Path = Path.Combine(root, "item.mkv"),
        });

        Assert.False(result.Allowed);
        Assert.Contains(nameof(SourceMutationKind.Delete), result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ManagedSource_DeniesPathOutsideConfiguredRoot()
    {
        var root = NewRoot();
        var result = _gate.Evaluate(new SourceMutationRequest
        {
            Source = Policy(root, FileSourceManagementMode.ManagedByTuvima),
            Mutation = SourceMutationKind.Move,
            Path = Path.Combine(Path.GetDirectoryName(root)!, "different-source", "item.mkv"),
        });

        Assert.False(result.Allowed);
        Assert.Contains("outside", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ManagedWritableSource_PermitsEnabledActionInsideRoot()
    {
        var root = NewRoot();
        var result = _gate.Evaluate(new SourceMutationRequest
        {
            Source = Policy(root, FileSourceManagementMode.ManagedByTuvima),
            Mutation = SourceMutationKind.MetadataWriteback,
            Path = Path.Combine(root, "item.flac"),
        });

        Assert.True(result.Allowed);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void ConfigurationFactory_DefaultsUnknownModesToExistingLibrarySafety()
    {
        var source = new LibrarySourceConfig
        {
            Id = "source",
            Path = NewRoot(),
            ManagementMode = "unexpected-mode",
            AccessMode = LibrarySourceAccessModes.Writable,
        };
        var library = new LibraryFolderConfig
        {
            Id = "library",
            Sources = [source],
            PrimaryDestinationSourceId = "source",
        };

        var policy = FileSourceMutationPolicyFactory.Create(
            library,
            source,
            allowDelete: true,
            isMergeTarget: true);

        Assert.Equal(FileSourceManagementMode.ExistingLibrary, policy.ManagementMode);
        Assert.False(policy.AllowMove);
        Assert.False(policy.AllowRename);
        Assert.False(policy.AllowMetadataWriteback);
        Assert.False(policy.AllowDelete);
        Assert.False(policy.AllowAsDestination);
    }

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "tuvima-source-policy", Guid.NewGuid().ToString("N"));

    internal static FileSourceMutationPolicy Policy(string root, FileSourceManagementMode mode, string sourceId = "source") => new()
    {
        LibraryId = "library",
        SourceId = sourceId,
        RootPath = root,
        ManagementMode = mode,
        IsWritable = true,
        AllowMove = true,
        AllowRename = true,
        AllowMetadataWriteback = true,
        AllowDelete = true,
        AllowAsDestination = true,
    };
}
