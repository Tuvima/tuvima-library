using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Services;

namespace MediaEngine.Ingestion.Tests;

/// <summary>
/// The safety gate for files in View. A managed View source (uploads, the Timeline, managed folder imports) may have its files
/// deleted beneath the owner's own profile folder; a linked source is a folder the person already had, so it is read-only.
/// </summary>
public sealed class ViewSourceMutationPolicyTests
{
    private readonly SourceMutationPolicyGate _gate = new();
    private readonly ViewPersonalSpace _space = new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "alice");

    private static string Root() => Path.Combine(Path.GetTempPath(), $"tuvima-view-policy-{Guid.NewGuid():N}", "Profiles", "alice");

    private ViewSource Source(ViewSourceStorageMode mode, Guid? spaceId = null) => new(
        Guid.NewGuid(), spaceId ?? _space.Id, ViewSourceType.BrowserUpload, "Uploads", "key", null,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, mode,
        mode == ViewSourceStorageMode.Managed ? "Profiles/alice/Timeline" : null,
        mode == ViewSourceStorageMode.Linked ? "D:/Photos" : null);

    private SourceMutationDecision Delete(FileSourceMutationPolicy policy, string path) =>
        _gate.Evaluate(new SourceMutationRequest { Source = policy, Mutation = SourceMutationKind.Delete, Path = path });

    [Fact]
    public void AManagedSourceFileInsideTheOwnersProfileFolder_MayBeDeleted()
    {
        var root = Root();
        var policy = ViewSourceMutationPolicyFactory.Create(_space, Source(ViewSourceStorageMode.Managed), root, storageIsWritable: true);

        Assert.Equal(FileSourceManagementMode.ManagedByTuvima, policy.ManagementMode);
        Assert.Equal(root, policy.RootPath);
        Assert.True(Delete(policy, Path.Combine(root, "Timeline", "beach.jpg")).Allowed);
    }

    [Fact]
    public void AManagedSourcePolicy_OnlyEverAllowsDelete()
    {
        var policy = ViewSourceMutationPolicyFactory.Create(_space, Source(ViewSourceStorageMode.Managed), Root(), storageIsWritable: true);

        Assert.True(policy.AllowDelete);
        Assert.False(policy.AllowMove);
        Assert.False(policy.AllowRename);
        Assert.False(policy.AllowMetadataWriteback);
        Assert.False(policy.AllowAsDestination);
        Assert.False(policy.IsMergeJob);
    }

    [Fact]
    public void APathOutsideTheOwnersProfileFolder_IsRefusedEvenInAManagedSource()
    {
        var root = Root();
        var policy = ViewSourceMutationPolicyFactory.Create(_space, Source(ViewSourceStorageMode.Managed), root, storageIsWritable: true);

        // Another person's folder beside it, a sibling that merely starts with the same letters, and a path that climbs out.
        Assert.False(Delete(policy, Path.Combine(Path.GetDirectoryName(root)!, "bob", "Timeline", "beach.jpg")).Allowed);
        Assert.False(Delete(policy, root + "-copy" + Path.DirectorySeparatorChar + "beach.jpg").Allowed);
        Assert.False(Delete(policy, Path.Combine(root, "..", "bob", "beach.jpg")).Allowed);
    }

    [Fact]
    public void ALinkedSourceFile_IsAlwaysRefusedAsReadOnly()
    {
        var root = Root();
        var policy = ViewSourceMutationPolicyFactory.Create(_space, Source(ViewSourceStorageMode.Linked), root, storageIsWritable: true);

        Assert.Equal(FileSourceManagementMode.ExistingLibrary, policy.ManagementMode);
        Assert.False(policy.AllowDelete);
        var verdict = Delete(policy, Path.Combine(root, "beach.jpg"));
        Assert.False(verdict.Allowed);
        Assert.Contains("read-only", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AManagedSourceOnStorageThatCannotBeWritten_IsRefused()
    {
        var root = Root();
        var policy = ViewSourceMutationPolicyFactory.Create(_space, Source(ViewSourceStorageMode.Managed), root, storageIsWritable: false);

        var verdict = Delete(policy, Path.Combine(root, "Timeline", "beach.jpg"));

        Assert.False(verdict.Allowed);
        Assert.Contains("not writable", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ASourceFromAnotherPersonalSpace_CannotBuildAPolicy()
    {
        var foreign = Source(ViewSourceStorageMode.Managed, spaceId: Guid.NewGuid());

        Assert.Throws<ArgumentException>(() => ViewSourceMutationPolicyFactory.Create(_space, foreign, Root(), storageIsWritable: true));
    }

    [Fact]
    public void ThePolicyNamesTheViewLibraryAndSourceByTheirStableIds()
    {
        var source = Source(ViewSourceStorageMode.Managed);

        var policy = ViewSourceMutationPolicyFactory.Create(_space, source, Root(), storageIsWritable: true);

        Assert.Equal(_space.LibraryId.ToString("D"), policy.LibraryId);
        Assert.Equal(source.Id.ToString("D"), policy.SourceId);
    }
}
