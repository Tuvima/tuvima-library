using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Approvals;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.AspNetCore.Http;

namespace MediaEngine.Api.Tests;

/// <summary>
/// A real <see cref="ApprovalService"/> over a temp SQLite store, with a fixed signed-in administrator and a clock the test
/// can move. Shared by the approval tests and the tests of the actions the approval rule guards.
/// </summary>
internal sealed class ApprovalTestKit : IDisposable
{
    public const string ApproverName = "Avery";

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"approvals-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;

    /// <param name="applications">The application store the service reads; a real one over the temp store when omitted.</param>
    /// <param name="permissions">The permission catalogue the service names permissions from; the real one when omitted.</param>
    /// <param name="records">The record store the service writes; the real one over the temp store when omitted.</param>
    public ApprovalTestKit(
        IApplicationRepository? applications = null,
        IPermissionRegistry? permissions = null,
        IApprovalRecordRepository? records = null)
    {
        DapperConfiguration.Configure();
        _database = new(_path);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        Records = new(_database);
        Clock = new();
        var profiles = new StubProfiles(AccountProfile());
        Service = new(records ?? Records, profiles, applications ?? new ApplicationRepository(_database), permissions ?? new PermissionRegistry(), Clock);
        Authority = new(PrincipalKind.Human, true, AccountId, ProfileId);
        Resolver = new FixedResolver(Authority);
    }

    public static Guid AccountId { get; } = Guid.NewGuid();

    public static Guid ProfileId { get; } = Guid.NewGuid();

    public ApprovalRecordRepository Records { get; }

    public MovableClock Clock { get; }

    public ApprovalService Service { get; }

    public RequestAuthority Authority { get; }

    public IRequestAuthorityResolver Resolver { get; }

    public DatabaseConnection Database => _database;

    public HttpContext Http() => new DefaultHttpContext();

    public void Dispose()
    {
        _database.Dispose();
        File.Delete(_path);
    }

    private static Profile AccountProfile() => new() { Id = ProfileId, DisplayName = ApproverName };

    public sealed class MovableClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 19, 9, 30, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FixedResolver(RequestAuthority authority) : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default) => ValueTask.FromResult(authority);
    }

    private sealed class StubProfiles(Profile profile) : IProfileRepository
    {
        public Task<IReadOnlyList<Profile>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Profile>>([profile]);

        public Task<Profile?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult<Profile?>(id == profile.Id ? profile : null);

        public Task<bool> UpdateAsync(Profile updated, CancellationToken ct = default) => Task.FromResult(false);
    }
}
