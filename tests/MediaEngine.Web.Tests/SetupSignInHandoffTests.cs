using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Tests;

public sealed class SetupSignInHandoffTests
{
    private static AuthSessionResponse Session() => new()
    {
        SessionToken = "token",
        Authority = new DashboardAuthorityResponse(Guid.NewGuid(), Guid.NewGuid(), true, true, 1, 1, true, false, null, 1, [], [], []),
    };

    [Fact]
    public void ACodeRedeemsTheSignInOnce()
    {
        var handoff = new SetupSignInHandoff();
        var session = Session();

        var code = handoff.Issue(session);

        Assert.Same(session, handoff.Redeem(code));
        Assert.Null(handoff.Redeem(code));
    }

    [Fact]
    public void AnUnknownEmptyOrExpiredCodeRedeemsNothing()
    {
        var clock = new MutableClock();
        var handoff = new SetupSignInHandoff(clock);
        var code = handoff.Issue(Session());

        Assert.Null(handoff.Redeem(null));
        Assert.Null(handoff.Redeem(string.Empty));
        Assert.Null(handoff.Redeem("not-a-code"));

        clock.Now = clock.Now.AddMinutes(3);
        Assert.Null(handoff.Redeem(code));
    }

    [Fact]
    public void EveryCodeIsLongAndDifferent()
    {
        var handoff = new SetupSignInHandoff();
        var codes = Enumerable.Range(0, 20).Select(_ => handoff.Issue(Session())).ToList();

        Assert.Equal(20, codes.Distinct().Count());
        Assert.All(codes, code => Assert.True(code.Length >= 40));
    }

    private sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
