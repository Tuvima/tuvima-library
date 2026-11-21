using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Services.Settings;

namespace MediaEngine.Web.Tests;

/// <summary>A hand-written stand-in for the "Share with friends" service: records what the card asked for and answers as told.</summary>
internal sealed class FriendInviteServiceSpy : IFriendInviteService
{
    public bool CanInvite { get; set; }

    public FriendInviteOptions Options { get; set; } = new(null, [], [], false);

    public List<FriendInviteRequest> Requests { get; } = [];

    public Func<FriendInviteRequest, FriendInviteResult>? Answer { get; set; }

    public Task<FriendInviteOptions> LoadAsync(CancellationToken ct = default) => Task.FromResult(Options);

    public Task<FriendInviteResult> CreateAsync(FriendInviteRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        return Task.FromResult(Answer?.Invoke(request) ?? new FriendInviteResult(
            true, null, FriendInviteLink.Build(Options.AwayAddress ?? "https://library.example.com", "ABCDE-FGHIJ"), "ABCDE-FGHIJ", DateTimeOffset.UtcNow.AddDays(7)));
    }
}
