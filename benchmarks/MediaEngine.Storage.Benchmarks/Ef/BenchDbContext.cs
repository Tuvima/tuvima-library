using MediaEngine.Domain.Entities;
using MediaEngine.Storage.Benchmarks.Queries;
using Microsoft.EntityFrameworkCore;

namespace MediaEngine.Storage.Benchmarks.Ef;

/// <summary>
/// Hand-written EF model for the tables the benchmark queries touch. Maps the production domain types
/// (<see cref="CanonicalValue"/>, <see cref="MetadataClaim"/>) directly, with explicit table and column names, so the
/// EF and Dapper variants return the same object type. No scaffolding, no migrations.
/// </summary>
internal sealed class BenchDbContext(DbContextOptions<BenchDbContext> options) : DbContext(options)
{
    public DbSet<CanonicalValue> CanonicalValues => Set<CanonicalValue>();

    public DbSet<MetadataClaim> MetadataClaims => Set<MetadataClaim>();

    public DbSet<CanonicalValueArrayRow> CanonicalValueArrays => Set<CanonicalValueArrayRow>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Same storage formats as DapperConfiguration: Guid as 16-byte BLOB, DateTimeOffset as ISO-8601 text.
        // They apply to mapped and keyless entity types. Ad-hoc SqlQueryRaw<T> types do NOT get them (EF then silently
        // reads the BLOB with its own byte order), which is why every raw-SQL row type below is a registered keyless
        // entity queried through Set<T>().FromSqlRaw.
        configurationBuilder.Properties<Guid>().HaveConversion<GuidBlobConverter>();
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetTextConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Raw-SQL result shapes: keyless entity types with no table, queried only through FromSqlRaw.
        modelBuilder.Entity<Q3LibraryWorkFeed.FeedRow>().HasNoKey().ToTable((string?)null);
        modelBuilder.Entity<Q3LibraryWorkFeed.ValueRow>().HasNoKey().ToTable((string?)null);
        modelBuilder.Entity<Q3LibraryWorkFeed.ArrayRow>().HasNoKey().ToTable((string?)null);
        modelBuilder.Entity<Q4LibraryItems.ItemRow>().HasNoKey().ToTable((string?)null);
        modelBuilder.Entity<DisplayWorkEfRow>().HasNoKey().ToTable((string?)null);

        modelBuilder.Entity<CanonicalValue>(entity =>
        {
            entity.ToTable("canonical_values");
            entity.HasKey(value => new { value.EntityId, value.Key });
            entity.Property(value => value.EntityId).HasColumnName("entity_id");
            entity.Property(value => value.Key).HasColumnName("key");
            entity.Property(value => value.Value).HasColumnName("value");
            entity.Property(value => value.LastScoredAt).HasColumnName("last_scored_at");
            entity.Property(value => value.IsConflicted).HasColumnName("is_conflicted");
            entity.Property(value => value.WinningProviderId).HasColumnName("winning_provider_id");
            entity.Property(value => value.NeedsReview).HasColumnName("needs_review");
        });

        modelBuilder.Entity<MetadataClaim>(entity =>
        {
            entity.ToTable("metadata_claims");
            entity.HasKey(claim => claim.Id);
            entity.Property(claim => claim.Id).HasColumnName("id");
            entity.Property(claim => claim.EntityId).HasColumnName("entity_id");
            entity.Property(claim => claim.ProviderId).HasColumnName("provider_id");
            entity.Property(claim => claim.DecisionSourceProviderId).HasColumnName("decision_source_provider_id");
            entity.Property(claim => claim.ObservationSetId).HasColumnName("observation_set_id");
            entity.Property(claim => claim.ClaimKey).HasColumnName("claim_key");
            entity.Property(claim => claim.ClaimValue).HasColumnName("claim_value");
            entity.Property(claim => claim.Confidence).HasColumnName("confidence");
            entity.Property(claim => claim.ClaimedAt).HasColumnName("claimed_at");
            entity.Property(claim => claim.IsUserLocked).HasColumnName("is_user_locked");
            entity.Property(claim => claim.IsCurrent).HasColumnName("is_current");
            entity.Property(claim => claim.SupersededAt).HasColumnName("superseded_at");
        });

        modelBuilder.Entity<CanonicalValueArrayRow>(entity =>
        {
            entity.ToTable("canonical_value_arrays");
            entity.HasKey(row => new { row.EntityId, row.Key, row.Ordinal });
            entity.Property(row => row.EntityId).HasColumnName("entity_id");
            entity.Property(row => row.Key).HasColumnName("key");
            entity.Property(row => row.Ordinal).HasColumnName("ordinal");
            entity.Property(row => row.Value).HasColumnName("value");
            entity.Property(row => row.ValueQid).HasColumnName("value_qid");
        });
    }
}

/// <summary>Row of <c>canonical_value_arrays</c> (multi-valued canonical metadata).</summary>
internal sealed class CanonicalValueArrayRow
{
    public Guid EntityId { get; set; }

    public string Key { get; set; } = string.Empty;

    public int Ordinal { get; set; }

    public string Value { get; set; } = string.Empty;

    public string? ValueQid { get; set; }
}
