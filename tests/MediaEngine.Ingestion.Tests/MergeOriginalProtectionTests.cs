using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Services;
using MediaEngine.Ingestion.Tests.Helpers;
using MediaEngine.Storage;

namespace MediaEngine.Ingestion.Tests;

/// <summary>
/// The single place a delete or move is allowed or refused (the mutation gate) must never let ordinary duplicate handling or
/// organising touch a file a folder merge listed as an original that stays in its source folder, whatever state that folder
/// is in now. Only the merge job itself may act on those files. Real temporary SQLite, hand-written spies.
/// </summary>
public sealed class MergeOriginalProtectionTests : IDisposable
{
    private readonly TestDatabaseFactory _db = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima_orig_{Guid.NewGuid():N}");
    private readonly FolderOrganiseLogRepository _ledger;

    public MergeOriginalProtectionTests()
    {
        Directory.CreateDirectory(_root);
        _ledger = new FolderOrganiseLogRepository(_db.Connection);
    }

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder that cannot be removed right now is left for the operating system's temp clean-up.
        }
    }

    private static readonly SourceMutationKind[] Dangerous = [SourceMutationKind.Delete, SourceMutationKind.Move, SourceMutationKind.Rename];

    [Theory]
    [InlineData(FolderOrganiseOutcomes.Copied)]
    [InlineData(FolderOrganiseOutcomes.CopiedWithUniqueName)]
    [InlineData(FolderOrganiseOutcomes.DuplicateSkipped)]
    [InlineData(FolderOrganiseOutcomes.SidecarCopied)]
    [InlineData(FolderOrganiseOutcomes.MergedForReview)]
    [InlineData(FolderOrganiseOutcomes.LeftInSource)]
    public async Task A_listed_original_is_refused_for_delete_move_and_rename_but_not_for_tags(string outcome)
    {
        var (policy, path) = await ListedAsync(outcome);
        var gate = new SourceMutationPolicyGate(_ledger);

        foreach (var kind in Dangerous)
        {
            var decision = gate.Evaluate(Request(policy, kind, path));
            Assert.False(decision.Allowed, $"{kind} must be refused for {outcome}");
            Assert.Contains("original of a folder merge", decision.Reason);
        }

        // Only deleting, moving and renaming are held back; the file stays readable and its tags are not this rule's business.
        Assert.True(gate.Evaluate(Request(policy, SourceMutationKind.MetadataWriteback, path)).Allowed);
    }

    [Theory]
    [InlineData(FolderOrganiseOutcomes.Moving)]
    [InlineData(FolderOrganiseOutcomes.Moved)]
    [InlineData(FolderOrganiseOutcomes.MovedWithUniqueName)]
    [InlineData(FolderOrganiseOutcomes.AlreadyCorrect)]
    [InlineData(FolderOrganiseOutcomes.DuplicateRemoved)]
    [InlineData(FolderOrganiseOutcomes.Skipped)]
    [InlineData(FolderOrganiseOutcomes.NotMoved)]
    [InlineData(FolderOrganiseOutcomes.Failed)]
    [InlineData(FolderOrganiseOutcomes.NeedsReview)]
    public async Task Lines_that_say_nothing_was_kept_behind_do_not_hold_a_file_back(string outcome)
    {
        // Conversions write to the same ledger; their lines (and a merge's moved or failed lines) must not freeze a file forever.
        var (policy, path) = await ListedAsync(outcome);
        var gate = new SourceMutationPolicyGate(_ledger);

        foreach (var kind in Dangerous)
        {
            Assert.True(gate.Evaluate(Request(policy, kind, path)).Allowed, $"{kind} must follow the usual rules for {outcome}");
        }
    }

    [Fact]
    public async Task The_lookup_ignores_letter_case_in_the_path()
    {
        var (policy, path) = await ListedAsync(FolderOrganiseOutcomes.Copied);
        var gate = new SourceMutationPolicyGate(_ledger);

        Assert.False(gate.Evaluate(Request(policy, SourceMutationKind.Delete, path.ToUpperInvariant())).Allowed);
    }

    [Fact]
    public async Task A_file_no_merge_listed_and_a_gate_without_a_ledger_follow_the_usual_rules()
    {
        var (policy, listed) = await ListedAsync(FolderOrganiseOutcomes.Copied);
        var other = Path.Combine(_root, "other.mkv");

        Assert.True(new SourceMutationPolicyGate(_ledger).Evaluate(Request(policy, SourceMutationKind.Delete, other)).Allowed);
        Assert.True(new SourceMutationPolicyGate().Evaluate(Request(policy, SourceMutationKind.Delete, listed)).Allowed);
    }

    [Fact]
    public async Task The_merge_jobs_own_policy_may_still_act_on_a_listed_original()
    {
        var (library, source) = ManagedLibrary();
        var path = Path.Combine(_root, "listed.mkv");
        await _ledger.UpsertAsync(new FolderOrganiseLogEntry(
            Guid.NewGuid(), Guid.Parse(source.Id), path, Path.Combine(_root, "copy.mkv"), FolderOrganiseOutcomes.Copied, DateTimeOffset.UtcNow));
        var gate = new SourceMutationPolicyGate(_ledger);

        var mergeJob = FileSourceMutationPolicyFactory.Create(library, source, allowDelete: true, isMergeJob: true);
        var everyoneElse = FileSourceMutationPolicyFactory.Create(library, source, allowDelete: true);

        Assert.True(mergeJob.IsMergeJob);
        Assert.False(everyoneElse.IsMergeJob);
        foreach (var kind in Dangerous)
        {
            Assert.True(gate.Evaluate(Request(mergeJob, kind, path)).Allowed);
            Assert.False(gate.Evaluate(Request(everyoneElse, kind, path)).Allowed);
        }
    }

    [Fact]
    public void A_folder_used_as_is_stays_read_only_whatever_the_merge_ledger_says()
    {
        var source = new LibrarySourceConfig
        {
            Id = "asis",
            Path = _root,
            ManagementMode = LibrarySourceManagementModes.ExistingLibrary,
            AccessMode = LibrarySourceAccessModes.ReadOnly,
        };
        var library = new LibraryFolderConfig { Id = "lib", Sources = [source] };
        var policy = FileSourceMutationPolicyFactory.Create(library, source, allowDelete: true);

        var decision = new SourceMutationPolicyGate(_ledger).Evaluate(Request(policy, SourceMutationKind.Delete, Path.Combine(_root, "x.mkv")));

        Assert.False(decision.Allowed);
        Assert.Contains("Read-only", decision.Reason);
    }

    [Fact]
    public async Task When_the_merge_ledger_cannot_be_read_the_file_is_kept()
    {
        var (policy, _) = await ListedAsync(FolderOrganiseOutcomes.Copied);
        var gate = new SourceMutationPolicyGate(new BrokenLedger());

        foreach (var kind in Dangerous)
        {
            var decision = gate.Evaluate(Request(policy, kind, Path.Combine(_root, "any.mkv")));
            Assert.False(decision.Allowed);
            Assert.Contains("left where it is", decision.Reason);
        }
    }

    [Fact]
    public async Task Only_the_latest_copy_line_of_the_same_folder_is_offered_to_a_restarted_merge()
    {
        var sourceId = Guid.NewGuid();
        var path = Path.Combine(_root, "a.mkv");
        await _ledger.UpsertAsync(new FolderOrganiseLogEntry(Guid.NewGuid(), sourceId, path, "old-copy", FolderOrganiseOutcomes.Copied, DateTimeOffset.UtcNow.AddHours(-2)));
        await _ledger.UpsertAsync(new FolderOrganiseLogEntry(Guid.NewGuid(), sourceId, path, "new-copy", FolderOrganiseOutcomes.CopiedWithUniqueName, DateTimeOffset.UtcNow.AddHours(-1)));
        await _ledger.UpsertAsync(new FolderOrganiseLogEntry(Guid.NewGuid(), Guid.NewGuid(), path, "other-folder", FolderOrganiseOutcomes.Copied, DateTimeOffset.UtcNow));
        await _ledger.UpsertAsync(new FolderOrganiseLogEntry(Guid.NewGuid(), sourceId, path, path, FolderOrganiseOutcomes.Failed, DateTimeOffset.UtcNow));

        var earlier = await _ledger.FindEarlierCopyAsync(sourceId, path);

        Assert.Equal("new-copy", earlier!.NewPath);
        Assert.Null(await _ledger.FindEarlierCopyAsync(sourceId, Path.Combine(_root, "never-seen.mkv")));
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private (LibraryFolderConfig Library, LibrarySourceConfig Source) ManagedLibrary()
    {
        var source = new LibrarySourceConfig
        {
            Id = Guid.NewGuid().ToString("D"),
            Path = _root,
            ManagementMode = LibrarySourceManagementModes.ManagedByTuvima,
            AccessMode = LibrarySourceAccessModes.Writable,
        };
        return (new LibraryFolderConfig { Id = "lib", Sources = [source], PrimaryDestinationSourceId = source.Id }, source);
    }

    private async Task<(FileSourceMutationPolicy Policy, string Path)> ListedAsync(string outcome)
    {
        var (library, source) = ManagedLibrary();
        var path = Path.Combine(_root, $"{outcome}.mkv");
        await _ledger.UpsertAsync(new FolderOrganiseLogEntry(
            Guid.NewGuid(), Guid.Parse(source.Id), path, Path.Combine(_root, "copy", $"{outcome}.mkv"), outcome, DateTimeOffset.UtcNow));
        return (FileSourceMutationPolicyFactory.Create(library, source, allowDelete: true), path);
    }

    private static SourceMutationRequest Request(FileSourceMutationPolicy policy, SourceMutationKind kind, string path) =>
        new() { Source = policy, Mutation = kind, Path = path };

    /// <summary>A merge ledger that cannot answer, to prove the gate keeps the file rather than guessing.</summary>
    private sealed class BrokenLedger : IFolderOrganiseLogRepository
    {
        public bool IsMergeOriginal(string path) => throw new InvalidOperationException("The data store is not available.");

        public Task UpsertAsync(FolderOrganiseLogEntry entry, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<FolderOrganiseLogEntry?> GetAsync(Guid operationId, string oldPath, CancellationToken ct = default) => throw new NotSupportedException();

        public Task DeleteAsync(Guid operationId, string oldPath, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<FolderOrganiseLogEntry>> ListUnfinishedAsync(Guid operationId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<HashSet<string>> GetFinishedPathsAsync(Guid operationId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<FolderOrganiseLogEntry>> ListAsync(Guid operationId, int offset, int limit, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<int> CountAsync(Guid operationId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, int>> CountByOutcomeAsync(Guid operationId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<FolderOrganiseLogEntry?> FindEarlierCopyAsync(Guid sourceId, string oldPath, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
