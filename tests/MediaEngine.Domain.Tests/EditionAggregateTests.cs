using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Domain.Tests;

public sealed class EditionAggregateTests
{
    [Fact]
    public void AggregateCollections_ExposeNonCastableReadOnlyViews()
    {
        var edition = new Edition();

        Assert.False(edition.MediaAssets is List<MediaAsset>);
        Assert.False(edition.MetadataClaims is List<MetadataClaim>);
        Assert.False(edition.CanonicalValues is List<CanonicalValue>);
    }

    [Fact]
    public void AddMediaAsset_AddsAssetAndIgnoresDuplicateId()
    {
        var assetId = Guid.NewGuid();
        var edition = new Edition();
        var asset = new MediaAsset { Id = assetId, EditionId = edition.Id };

        edition.AddMediaAsset(asset);
        edition.AddMediaAsset(new MediaAsset { Id = assetId, EditionId = edition.Id });

        Assert.Single(edition.MediaAssets);
        Assert.Same(asset, edition.MediaAssets[0]);
    }

    [Fact]
    public void AddMediaAsset_RejectsNull()
    {
        var edition = new Edition();

        Assert.Throws<ArgumentNullException>(() => edition.AddMediaAsset(null!));
    }

    [Fact]
    public void AddMetadataClaim_PreservesAppendOnlyBehavior()
    {
        var edition = new Edition();
        var first = new MetadataClaim { Id = Guid.NewGuid(), EntityId = edition.Id, ClaimKey = "runtime", ClaimValue = "100" };
        var second = new MetadataClaim { Id = Guid.NewGuid(), EntityId = edition.Id, ClaimKey = "runtime", ClaimValue = "101" };

        edition.AddMetadataClaim(first);
        edition.AddMetadataClaim(second);

        Assert.Equal(2, edition.MetadataClaims.Count);
    }

    [Fact]
    public void AddCanonicalValue_PreservesCurrentDuplicateKeyBehavior()
    {
        var edition = new Edition();

        edition.AddCanonicalValue(new CanonicalValue { EntityId = edition.Id, Key = "runtime", Value = "100" });
        edition.AddCanonicalValue(new CanonicalValue { EntityId = edition.Id, Key = "runtime", Value = "101" });

        Assert.Equal(2, edition.CanonicalValues.Count);
    }

    [Fact]
    public void MoveToWork_ReparentsTheEdition_AndIsANoOpWhenAlreadyThere()
    {
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        var edition = new Edition { Id = Guid.NewGuid(), WorkId = source };

        Assert.True(edition.MoveToWork(target));
        Assert.Equal(target, edition.WorkId);
        Assert.False(edition.MoveToWork(target));
        Assert.Throws<ArgumentException>(() => edition.MoveToWork(Guid.Empty));
    }

    [Fact]
    public void WorkMarkOwned_PromotesCatalogRows_AndKeepsOtherKinds()
    {
        var catalog = new Work { WorkKind = MediaEngine.Domain.Enums.WorkKind.Catalog, IsCatalogOnly = true };
        var parent = new Work { WorkKind = MediaEngine.Domain.Enums.WorkKind.Parent, IsCatalogOnly = true };

        catalog.MarkOwned();
        parent.MarkOwned();

        Assert.Equal(MediaEngine.Domain.Enums.WorkKind.Child, catalog.WorkKind);
        Assert.False(catalog.IsCatalogOnly);
        Assert.Equal(MediaEngine.Domain.Enums.WorkKind.Parent, parent.WorkKind);
        Assert.Equal(MediaEngine.Domain.Enums.OwnershipStatus.Owned, parent.Ownership);
    }

    [Fact]
    public void MediaAssetAssignToLibrary_ChangesOnlyTheLibrary()
    {
        var asset = new MediaAsset { FilePathRoot = @"C:\media.mkv", LibraryId = "movies" };

        asset.AssignToLibrary("tv");

        Assert.Equal("tv", asset.LibraryId);
        Assert.Equal(@"C:\media.mkv", asset.FilePathRoot);
        Assert.Throws<ArgumentException>(() => asset.AssignToLibrary(" "));
    }
}
