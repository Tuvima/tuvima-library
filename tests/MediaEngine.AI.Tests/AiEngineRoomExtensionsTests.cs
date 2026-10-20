using MediaEngine.AI.EngineRoom;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Jobs;

namespace MediaEngine.AI.Tests;

/// <summary>Call sites keep seeing what the old inference service gave them: a value, null for an unreadable answer, or an exception.</summary>
public sealed class AiEngineRoomExtensionsTests
{
    private static readonly AiCaller Caller = AiCaller.LibraryBackground("test_feature");

    private sealed record Answer(string? Title);

    [Fact]
    public async Task ExtractJsonOrThrow_ReturnsNull_WhenTheModelAnsweredButNotWithTheJsonAskedFor()
    {
        var room = StubAiEngineRoom.ReturningJson("not json at all");

        var result = await room.ExtractJsonOrThrowAsync<Answer>(Caller, AiAbility.Classify, "p", "g");

        Assert.Null(result);
    }

    [Fact]
    public async Task ExtractJsonOrThrow_ReturnsTheValue_OnSuccess()
    {
        var room = StubAiEngineRoom.ReturningJson("""{"title":"Dune"}""");

        var result = await room.ExtractJsonOrThrowAsync<Answer>(Caller, AiAbility.Classify, "p", "g");

        Assert.Equal("Dune", result?.Title);
    }

    [Fact]
    public async Task WriteTextOrThrow_ReturnsTheText_OnSuccess()
    {
        var room = StubAiEngineRoom.ReturningJson("hello");

        Assert.Equal("hello", await room.WriteTextOrThrowAsync(Caller, AiAbility.WriteTextQuick, "p"));
    }

    [Fact]
    public async Task Unavailable_BecomesTheUnavailableCapabilityException()
    {
        var room = StubAiEngineRoom.Failing(AiRequestStatus.Unavailable, "Model not downloaded");

        var error = await Assert.ThrowsAsync<UnavailableCapabilityException>(
            () => room.WriteTextOrThrowAsync(Caller, AiAbility.WriteTextQuick, "p"));

        Assert.Equal("Model not downloaded", error.Message);
    }

    [Fact]
    public async Task Deferred_BecomesTheDeferredException_SoBatchesCanSkipAndRetry()
    {
        var room = StubAiEngineRoom.Failing(AiRequestStatus.Deferred, "Waiting: the machine is busy");

        await Assert.ThrowsAsync<AiRequestDeferredException>(
            () => room.ExtractJsonOrThrowAsync<Answer>(Caller, AiAbility.Classify, "p", "g"));
    }

    [Fact]
    public async Task RefusedOverBudget_BecomesTheRefusedException()
    {
        var room = StubAiEngineRoom.Failing(AiRequestStatus.RefusedOverBudget, "Used its Local AI allowance for this hour");

        await Assert.ThrowsAsync<AiRequestRefusedException>(
            () => room.WriteTextOrThrowAsync(Caller, AiAbility.WriteTextQuick, "p"));
    }

    [Fact]
    public async Task TimedOut_BecomesATimeoutException()
    {
        var room = StubAiEngineRoom.Failing(AiRequestStatus.TimedOut, "Inference timed out.");

        await Assert.ThrowsAsync<TimeoutException>(
            () => room.WriteTextOrThrowAsync(Caller, AiAbility.WriteTextQuick, "p"));
    }

    [Fact]
    public async Task Failed_BecomesAnInvalidOperationException()
    {
        var room = StubAiEngineRoom.Failing(AiRequestStatus.Failed, "boom");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => room.WriteTextOrThrowAsync(Caller, AiAbility.WriteTextQuick, "p"));

        Assert.Equal("boom", error.Message);
    }

    [Fact]
    public async Task CancelledByTheCaller_BecomesAnOperationCanceledExceptionCarryingTheirToken()
    {
        using var source = new CancellationTokenSource();
        var room = StubAiEngineRoom.Failing(AiRequestStatus.Cancelled, "Cancelled");

        var error = await Assert.ThrowsAsync<OperationCanceledException>(
            () => room.WriteTextOrThrowAsync(Caller, AiAbility.WriteTextQuick, "p", ct: source.Token));

        Assert.IsNotType<AiRequestPreemptedException>(error);
        Assert.Equal(source.Token, error.CancellationToken);
    }

    [Fact]
    public async Task CancelledByAPerson_BecomesThePreemptedException()
    {
        var room = StubAiEngineRoom.Failing(new AiRequestOutcome(AiRequestStatus.Cancelled, "Paused", Preempted: true));

        await Assert.ThrowsAsync<AiRequestPreemptedException>(
            () => room.WriteTextOrThrowAsync(Caller, AiAbility.WriteTextQuick, "p"));
    }
}
