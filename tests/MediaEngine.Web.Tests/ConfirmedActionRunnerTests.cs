using System.Net;
using System.Text;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Tests;

/// <summary>The "Confirm it's you" retry rule: ask once, retry the action once, never loop.</summary>
public sealed class ConfirmedActionRunnerTests
{
    private static DashboardAccessMutationResult Refused() =>
        DashboardAccessMutationResult.FailureResult(DashboardAccessMutationFailure.ConfirmItsYou, HttpStatusCode.Forbidden);

    [Fact]
    public async Task AnActionThatSucceeds_RunsOnceAndNeverAsks()
    {
        var confirmer = new SpyConfirmer(confirmed: true);
        var runner = new ConfirmedActionRunner(confirmer);
        var calls = 0;

        var result = await runner.RunAsync(() => { calls++; return Task.FromResult(DashboardAccessMutationResult.Success()); });

        Assert.True(result.Succeeded);
        Assert.Equal(1, calls);
        Assert.Equal(0, confirmer.Asked);
    }

    [Fact]
    public async Task AConfirmItsYouRefusal_AsksOnce_ThenRetriesTheActionOnce()
    {
        var confirmer = new SpyConfirmer(confirmed: true);
        var runner = new ConfirmedActionRunner(confirmer);
        var calls = 0;

        var result = await runner.RunAsync(() =>
        {
            calls++;
            return Task.FromResult(calls == 1 ? Refused() : DashboardAccessMutationResult.Success());
        });

        Assert.True(result.Succeeded);
        Assert.Equal(2, calls);
        Assert.Equal(1, confirmer.Asked);
    }

    [Fact]
    public async Task WhenThePersonCancels_TheActionIsNotRetried_AndTheRefusalIsReturned()
    {
        var confirmer = new SpyConfirmer(confirmed: false);
        var runner = new ConfirmedActionRunner(confirmer);
        var calls = 0;

        var result = await runner.RunAsync(() => { calls++; return Task.FromResult(Refused()); });

        Assert.Equal(DashboardAccessMutationFailure.ConfirmItsYou, result.Failure);
        Assert.Equal(1, calls);
        Assert.Equal(1, confirmer.Asked);
    }

    [Fact]
    public async Task IfTheEngineStillRefusesAfterConfirming_ItIsNotAskedAgain()
    {
        var confirmer = new SpyConfirmer(confirmed: true);
        var runner = new ConfirmedActionRunner(confirmer);
        var calls = 0;

        var result = await runner.RunAsync(() => { calls++; return Task.FromResult(Refused()); });

        Assert.Equal(DashboardAccessMutationFailure.ConfirmItsYou, result.Failure);
        Assert.Equal(2, calls);
        Assert.Equal(1, confirmer.Asked);
    }

    [Theory]
    [InlineData(DashboardAccessMutationFailure.Conflict)]
    [InlineData(DashboardAccessMutationFailure.Forbidden)]
    [InlineData(DashboardAccessMutationFailure.Transient)]
    public async Task OtherFailures_AreReturnedWithoutAskingOrRetrying(DashboardAccessMutationFailure failure)
    {
        var confirmer = new SpyConfirmer(confirmed: true);
        var runner = new ConfirmedActionRunner(confirmer);
        var calls = 0;

        var result = await runner.RunAsync(() =>
        {
            calls++;
            return Task.FromResult(DashboardAccessMutationResult.FailureResult(failure));
        });

        Assert.Equal(failure, result.Failure);
        Assert.Equal(1, calls);
        Assert.Equal(0, confirmer.Asked);
    }

    [Fact]
    public async Task AResultThatCarriesAValue_IsRetriedTheSameWay()
    {
        var confirmer = new SpyConfirmer(confirmed: true);
        var runner = new ConfirmedActionRunner(confirmer);
        var calls = 0;

        var result = await runner.RunAsync(() =>
        {
            calls++;
            return Task.FromResult(calls == 1
                ? DashboardAccessMutationResult<string>.FailureResult(DashboardAccessMutationFailure.ConfirmItsYou)
                : DashboardAccessMutationResult<string>.Success("codes"));
        });

        Assert.Equal("codes", result.Value);
        Assert.Equal(2, calls);
        Assert.Equal(1, confirmer.Asked);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "{\"code\":\"confirm_its_you\",\"status\":403}", true)]
    [InlineData(HttpStatusCode.Forbidden, "{\"status\":403}", false)]
    [InlineData(HttpStatusCode.Forbidden, "<html>blocked by a proxy</html>", false)]
    [InlineData(HttpStatusCode.Conflict, "{\"code\":\"confirm_its_you\"}", false)]
    [InlineData(HttpStatusCode.Forbidden, "{\"code\":\"secure_account_first\"}", false)]
    public async Task OnlyA403WithTheConfirmItsYouCode_CountsAsAConfirmItsYouRefusal(HttpStatusCode status, string body, bool expected)
    {
        using var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        Assert.Equal(expected, await ConfirmItsYouRequests.IsConfirmItsYouRefusalAsync(response, CancellationToken.None));
        // The body is still readable afterwards.
        Assert.Equal(body, await response.Content.ReadAsStringAsync());
    }

    internal sealed class SpyConfirmer(bool confirmed) : IItsYouConfirmer
    {
        public int Asked { get; private set; }

        public Task<bool> ConfirmAsync(CancellationToken ct = default)
        {
            Asked++;
            return Task.FromResult(confirmed);
        }
    }
}
