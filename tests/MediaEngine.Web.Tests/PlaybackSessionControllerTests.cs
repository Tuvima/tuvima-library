using System.Net;
using System.Net.Http.Json;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Tests;

public sealed class PlaybackSessionControllerTests
{
    [Fact]
    public async Task AudiobookAdvancesToNextOriginalFileAndChapterSelectionUsesItsAsset()
    {
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var service = new PlaybackSessionController(null!, null!);
        var book = CreateAudiobookItem("One recording", $"/stream/{first:D}") with
        {
            AssetId = first,
            Chapters = [new() {Index=0,AssetId=first,Title="Original part one",StartSeconds=0,EndSeconds=60},
                        new() {Index=1,AssetId=second,Title="Original part two",StartSeconds=0,EndSeconds=90}],
        };
        await service.PlayAudiobookAsync(book);
        Assert.Equal("Original part one",service.CurrentChapter!.Title);
        await service.SetPlaybackRateAsync(1.75d);
        await service.CompleteCurrentAsync();
        Assert.Equal(second,service.CurrentItem!.AssetId);
        Assert.Equal(1.75d, service.PlaybackRate);
        Assert.Equal($"/engine-stream/{second:D}",service.CurrentBrowserStreamUrl);
        Assert.Equal("Original part two",service.CurrentChapter!.Title);
        await service.PlayAudiobookChapterAsync(0);
        Assert.Equal(first,service.CurrentItem!.AssetId);
        Assert.Equal(1.75d, service.PlaybackRate);
        Assert.Equal($"/engine-stream/{first:D}",service.CurrentBrowserStreamUrl);

        await service.PlayAudiobookAsync(CreateAudiobookItem("Different book", "stream://different-book"));
        Assert.Equal(1.25d, service.PlaybackRate);
    }

    [Fact]
    public async Task EndNextSleepArmKeepsCapturedCrossAssetTargetAndRejectsInvalidReplacementWithoutLosingArm()
    {
        var profileId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var firstAsset = Guid.NewGuid();
        var secondAsset = Guid.NewGuid();
        var item = CreateAudiobookItem("Book", "stream://book") with
        {
            WorkId = workId,
            AudiobookWorkId = workId,
            AssetId = firstAsset,
            Chapters =
            [
                new() { Index = 28, AssetId = firstAsset, Title = "Chapter 29", StartSeconds = 0, EndSeconds = 2199 },
                new() { Index = 29, AssetId = secondAsset, Title = "Chapter 30", StartSeconds = 0, EndSeconds = 837 },
            ],
        };
        var playback = new PlaybackSessionController(null!, null!, preferences: new ActiveProfilePlaybackPreferences(profileId));
        playback.RestoreState(new ListenPlaybackSnapshot
        {
            Queue = [item],
            CurrentIndex = 0,
            Experience = PlayerExperienceModes.Audiobook,
            CurrentTimeSeconds = 1020,
            DurationSeconds = 2199,
        });
        var arms = new List<AudiobookSleepTimerStateDto>();
        playback.SleepTimerNativePositionRequested += (_, _) => Task.FromResult<double?>(1020);
        playback.SleepTimerNativeArmRequested += state =>
        {
            arms.Add(state);
            return Task.FromResult(true);
        };

        var armed = await playback.SetAudiobookSleepTimerAsync(profileId, workId, firstAsset,
            playback.PlaybackRequestVersion, new AudiobookSleepTimerSelectionDto { Mode = AudiobookSleepTimerModes.EndNext });
        Assert.Equal(secondAsset, armed.TargetAssetId);
        Assert.Equal(29, armed.TargetChapterIndex);
        Assert.Equal(837, armed.TargetEndSeconds);
        Assert.True(playback.SleepTimerAvailability.CanEndNext);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => playback.SetAudiobookSleepTimerAsync(profileId,
            workId, firstAsset, playback.PlaybackRequestVersion,
            new AudiobookSleepTimerSelectionDto { Mode = AudiobookSleepTimerModes.Timer, Minutes = 7 }));
        Assert.Equal(armed, playback.SleepTimerState);

        await playback.HandleNativeSleepTimerExpiredAsync(armed.TimerGeneration, firstAsset,
            armed.PlaybackRequestVersion, 2199);
        Assert.True(armed.TimerGeneration == playback.SleepTimerState.TimerGeneration,
            "The origin chapter's longer local duration cannot expire the target asset timer.");

        await playback.PlayAudiobookChapterAsync(item, item.Chapters[1]);
        Assert.Equal(armed.TimerGeneration, playback.SleepTimerState.TimerGeneration);
        Assert.Equal(secondAsset, playback.SleepTimerState.BoundAssetId);
        Assert.Equal(playback.PlaybackRequestVersion, playback.SleepTimerState.PlaybackRequestVersion);
        Assert.Contains(arms, state => state.TimerGeneration == armed.TimerGeneration && state.BoundAssetId == secondAsset);
        Assert.Equal(playback.SleepTimerState, playback.CreateSnapshot().SleepTimerState);

        await playback.HandleNativeSleepTimerExpiredAsync(armed.TimerGeneration, secondAsset,
            playback.PlaybackRequestVersion, 837);
        Assert.Equal(AudiobookSleepTimerModes.Off, playback.SleepTimerState.Mode);
    }

    [Fact]
    public async Task NativeEndOfFileConfirmsRoundedChapterEndButRejectsStaleProof()
    {
        var profileId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var item = CreateAudiobookItem("Short audiobook", "stream://short") with
        {
            WorkId = workId,
            AudiobookWorkId = workId,
            AssetId = assetId,
            Duration = "0:21",
            Chapters = [new() { Index = 0, AssetId = assetId, Title = "Intro", StartSeconds = 0, EndSeconds = 21 }],
        };
        var playback = new PlaybackSessionController(null!, null!, preferences: new ActiveProfilePlaybackPreferences(profileId));
        playback.RestoreState(new ListenPlaybackSnapshot
        {
            Queue = [item], CurrentIndex = 0, Experience = PlayerExperienceModes.Audiobook, DurationSeconds = 21,
        });
        playback.SleepTimerNativePositionRequested += (_, _) => Task.FromResult<double?>(0);
        playback.SleepTimerNativeArmRequested += _ => Task.FromResult(true);

        var arm = await playback.SetAudiobookSleepTimerAsync(profileId, workId, assetId,
            playback.PlaybackRequestVersion,
            new AudiobookSleepTimerSelectionDto { Mode = AudiobookSleepTimerModes.EndCurrent });

        await playback.HandleNativeSleepTimerExpiredAsync(arm.TimerGeneration + 1, assetId,
            arm.PlaybackRequestVersion, 20.700998, nativeEndOfFileConfirmed: true);
        await playback.HandleNativeSleepTimerExpiredAsync(arm.TimerGeneration, Guid.NewGuid(),
            arm.PlaybackRequestVersion, 20.700998, nativeEndOfFileConfirmed: true);
        await playback.HandleNativeSleepTimerExpiredAsync(arm.TimerGeneration, assetId,
            arm.PlaybackRequestVersion, double.NaN, nativeEndOfFileConfirmed: true);
        Assert.Equal(AudiobookSleepTimerModes.EndCurrent, playback.SleepTimerState.Mode);
        Assert.Equal(0, playback.CurrentTimeSeconds);

        await playback.HandleNativeSleepTimerExpiredAsync(arm.TimerGeneration, assetId,
            arm.PlaybackRequestVersion, 20.700998, nativeEndOfFileConfirmed: true);
        Assert.Equal(AudiobookSleepTimerModes.Off, playback.SleepTimerState.Mode);
        Assert.Equal(20.700998, playback.CurrentTimeSeconds, 6);
        Assert.False(playback.IsPlaying);
    }

    [Fact]
    public async Task NativeAudioEndedAdvancesWithoutTimerAndRejectsReplacedSubject()
    {
        var first = CreateQueueItem("First", "stream://first") with { AssetId = Guid.NewGuid() };
        var second = CreateQueueItem("Second", "stream://second") with { AssetId = Guid.NewGuid() };
        var playback = new PlaybackSessionController(null!, null!);
        playback.RestoreState(new ListenPlaybackSnapshot
        {
            Queue = [first, second], CurrentIndex = 0, Experience = PlayerExperienceModes.Music,
        });
        var endedAsset = first.AssetId!.Value;
        var endedRequest = playback.PlaybackRequestVersion;
        var generation = playback.SleepTimerState.TimerGeneration;

        await playback.HandleNativeAudioEndedAsync(endedAsset, endedRequest, generation, 210);
        Assert.Equal(second.AssetId, playback.CurrentItem?.AssetId);
        var replacementPosition = playback.CurrentTimeSeconds;

        await playback.HandleNativeAudioEndedAsync(endedAsset, endedRequest, generation, 210);
        Assert.Equal(second.AssetId, playback.CurrentItem?.AssetId);
        Assert.Equal(replacementPosition, playback.CurrentTimeSeconds);
    }

    [Fact]
    public async Task NativeEndOfFileProjectsPositionAndStopsOwnerBeforeCompletion()
    {
        var item = CreateQueueItem("Final song", "stream://final") with
        {
            AssetId = Guid.NewGuid(),
            Duration = "4:00",
        };
        var playback = new PlaybackSessionController(null!, null!);
        playback.RestoreState(new ListenPlaybackSnapshot
        {
            Queue = [item], CurrentIndex = 0, Experience = PlayerExperienceModes.Music,
            IsPlaying = true, CurrentTimeSeconds = 203,
        });
        var endedAsset = item.AssetId!.Value;
        var endedRequest = playback.PlaybackRequestVersion;
        var generation = playback.SleepTimerState.TimerGeneration;

        await playback.HandleNativeAudioEndedAsync(endedAsset, endedRequest, generation, 239.4);

        Assert.Equal(239.4, playback.CurrentTimeSeconds, 6);
        Assert.False(playback.IsPlaying);
    }

    [Fact]
    public async Task SameBookChapterContinuationPublishesExactRateWhileSettingsArePending()
    {
        var settings = new DelayNextPlaybackSettings();
        var playback = new PlaybackSessionController(null!, null!, preferences: settings);
        var firstAsset = Guid.NewGuid();
        var secondAsset = Guid.NewGuid();
        var book = CreateAudiobookItem("Book", "stream://book") with
        {
            AssetId = firstAsset,
            Chapters =
            [
                new() { Index = 0, AssetId = firstAsset, Title = "Part one", StartSeconds = 0, EndSeconds = 60 },
                new() { Index = 1, AssetId = secondAsset, Title = "Part two", StartSeconds = 0, EndSeconds = 90 },
            ],
        };
        await playback.PlayAudiobookAsync(book);
        await playback.SetPlaybackRateAsync(1.25d);
        settings.BlockNextCall();
        PlaybackSessionState? continuationProjection = null;
        playback.Changed += _ =>
        {
            if (playback.CurrentItem?.AssetId == secondAsset && continuationProjection is null)
            {
                continuationProjection = playback.State;
            }
        };

        var continuation = playback.PlayAudiobookChapterAsync(book, book.Chapters[1]);
        await settings.BlockedCallEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(continuationProjection);
        Assert.Equal(1.25d, continuationProjection.PlaybackRate);
        Assert.Equal(1.25d, playback.PlaybackRate);
        settings.ReleaseBlockedCall();
        await continuation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1.25d, playback.PlaybackRate);
    }

    [Fact]
    public async Task EndCurrentUsesFreshNativeChapterPositionInsteadOfLastControllerTick()
    {
        var profileId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var item = CreateAudiobookItem("Book", "stream://book") with
        {
            WorkId = workId,
            AudiobookWorkId = workId,
            AssetId = assetId,
            Chapters =
            [
                new() { Index = 0, AssetId = assetId, Title = "Chapter one", StartSeconds = 0, EndSeconds = 100 },
                new() { Index = 1, AssetId = assetId, Title = "Chapter two", StartSeconds = 100, EndSeconds = 200 },
            ],
        };
        var playback = new PlaybackSessionController(null!, null!, preferences: new ActiveProfilePlaybackPreferences(profileId));
        playback.RestoreState(new ListenPlaybackSnapshot
        {
            Queue = [item],
            CurrentIndex = 0,
            Experience = PlayerExperienceModes.Audiobook,
            CurrentTimeSeconds = 12,
            DurationSeconds = 200,
        });
        playback.SleepTimerNativePositionRequested += (_, _) => Task.FromResult<double?>(130);
        playback.SleepTimerNativeArmRequested += _ => Task.FromResult(true);

        var armed = await playback.SetAudiobookSleepTimerAsync(profileId, workId, assetId,
            playback.PlaybackRequestVersion, new AudiobookSleepTimerSelectionDto { Mode = AudiobookSleepTimerModes.EndCurrent });

        Assert.Equal(1, armed.OriginChapterIndex);
        Assert.Equal(1, armed.TargetChapterIndex);
        Assert.Equal("Chapter two", armed.TargetChapterTitle);
        Assert.Equal(200, armed.TargetEndSeconds);
        await playback.SetAudiobookSleepTimerAsync(profileId, workId, assetId,
            playback.PlaybackRequestVersion, new AudiobookSleepTimerSelectionDto { Mode = AudiobookSleepTimerModes.Off });
    }

    [Fact]
    public async Task ManualChapterSelectionPastCapturedBoundaryKeepsOriginalTargetUntilExpiry()
    {
        var profileId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var item = CreateAudiobookItem("Book", "stream://book") with
        {
            WorkId = workId,
            AudiobookWorkId = workId,
            AssetId = assetId,
            Chapters =
            [
                new() { Index = 0, AssetId = assetId, Title = "Chapter one", StartSeconds = 0, EndSeconds = 100 },
                new() { Index = 1, AssetId = assetId, Title = "Chapter two", StartSeconds = 100, EndSeconds = 200 },
                new() { Index = 2, AssetId = assetId, Title = "Chapter three", StartSeconds = 240, EndSeconds = 360 },
            ],
        };
        var playback = new PlaybackSessionController(null!, null!, preferences: new ActiveProfilePlaybackPreferences(profileId));
        playback.RestoreState(new ListenPlaybackSnapshot
        {
            Queue = [item], CurrentIndex = 0, Experience = PlayerExperienceModes.Audiobook,
            CurrentTimeSeconds = 130, DurationSeconds = 360,
        });
        playback.SleepTimerNativePositionRequested += (_, _) => Task.FromResult<double?>(130);
        playback.SleepTimerNativeArmRequested += _ => Task.FromResult(true);

        var armed = await playback.SetAudiobookSleepTimerAsync(profileId, workId, assetId,
            playback.PlaybackRequestVersion,
            new AudiobookSleepTimerSelectionDto { Mode = AudiobookSleepTimerModes.EndCurrent });

        await playback.PlayAudiobookChapterAsync(item, item.Chapters[2]);

        Assert.Equal(AudiobookSleepTimerModes.EndCurrent, playback.SleepTimerState.Mode);
        Assert.Equal(armed.TimerGeneration, playback.SleepTimerState.TimerGeneration);
        Assert.Equal(armed.TargetAssetId, playback.SleepTimerState.TargetAssetId);
        Assert.Equal(armed.TargetChapterIndex, playback.SleepTimerState.TargetChapterIndex);
        Assert.Equal(armed.TargetEndSeconds, playback.SleepTimerState.TargetEndSeconds);
        Assert.Equal(240, playback.CurrentTimeSeconds);

        await playback.HandleNativeSleepTimerExpiredAsync(armed.TimerGeneration, assetId,
            playback.PlaybackRequestVersion, playback.CurrentTimeSeconds);
        Assert.Equal(AudiobookSleepTimerModes.Off, playback.SleepTimerState.Mode);
    }

    [Fact]
    public async Task ProfileOrAuthorityInvalidationClearsTimerAndItsNativeArm()
    {
        var originalProfileId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var preferences = new MutableActiveProfilePlaybackPreferences(originalProfileId);
        var item = CreateAudiobookItem("Book", "stream://book") with
        {
            WorkId = workId, AudiobookWorkId = workId, AssetId = assetId,
        };
        var playback = new PlaybackSessionController(null!, null!, preferences: preferences);
        playback.RestoreState(new ListenPlaybackSnapshot
        {
            Queue = [item], CurrentIndex = 0, Experience = PlayerExperienceModes.Audiobook,
        });
        var nativeStates = new List<AudiobookSleepTimerStateDto>();
        playback.SleepTimerNativeArmRequested += state =>
        {
            nativeStates.Add(state);
            return Task.FromResult(true);
        };
        var armed = await playback.SetAudiobookSleepTimerAsync(originalProfileId, workId, assetId,
            playback.PlaybackRequestVersion,
            new AudiobookSleepTimerSelectionDto { Mode = AudiobookSleepTimerModes.Timer, Minutes = 15 });
        Assert.Equal(0, armed.PlaybackRequestVersion);

        preferences.ActiveProfileId = Guid.NewGuid();
        await playback.InvalidateAudiobookSleepTimerAsync();

        Assert.Equal(AudiobookSleepTimerModes.Off, playback.SleepTimerState.Mode);
        Assert.True(playback.SleepTimerState.TimerGeneration > armed.TimerGeneration);
        Assert.Contains(nativeStates, state => state.Mode == AudiobookSleepTimerModes.Off
            && state.BoundAssetId == assetId
            && state.PlaybackRequestVersion == armed.PlaybackRequestVersion
            && state.TimerGeneration == playback.SleepTimerState.TimerGeneration);
        await playback.HandleNativeSleepTimerExpiredAsync(armed.TimerGeneration, assetId,
            armed.PlaybackRequestVersion, double.PositiveInfinity);
        Assert.Equal(AudiobookSleepTimerModes.Off, playback.SleepTimerState.Mode);
    }

    [Fact]
    public async Task AuthorityInvalidationCancelsPendingNativeArmBeforeItCanCommit()
    {
        var profileId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var preferences = new MutableActiveProfilePlaybackPreferences(profileId);
        var item = CreateAudiobookItem("Book", "stream://book") with
        {
            WorkId = workId,
            AudiobookWorkId = workId,
            AssetId = assetId,
        };
        var playback = new PlaybackSessionController(null!, null!, preferences: preferences);
        playback.RestoreState(new ListenPlaybackSnapshot
        {
            Queue = [item], CurrentIndex = 0, Experience = PlayerExperienceModes.Audiobook,
        });
        var candidateStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var candidateBinding = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var nativeStates = new List<AudiobookSleepTimerStateDto>();
        AudiobookSleepTimerStateDto? pendingCandidate = null;
        playback.SleepTimerNativeArmRequested += state =>
        {
            nativeStates.Add(state);
            if (state.Mode != AudiobookSleepTimerModes.Off)
            {
                pendingCandidate = state;
                Assert.True(playback.CanBindNativeSleepTimerState(state));
                candidateStarted.TrySetResult();
                return candidateBinding.Task;
            }

            Assert.True(playback.CanBindNativeSleepTimerState(state));
            return Task.FromResult(true);
        };

        var pendingSelection = playback.SetAudiobookSleepTimerAsync(profileId, workId, assetId,
            playback.PlaybackRequestVersion,
            new AudiobookSleepTimerSelectionDto { Mode = AudiobookSleepTimerModes.Timer, Minutes = 15 });
        await candidateStarted.Task;

        Assert.True(playback.SleepTimerRegistrationInProgress);
        Assert.Equal(AudiobookSleepTimerModes.Off, playback.SleepTimerState.Mode);

        preferences.ActiveProfileId = Guid.NewGuid();
        await playback.InvalidateAudiobookSleepTimerAsync();
        var invalidationGeneration = playback.SleepTimerState.TimerGeneration;

        Assert.Equal(AudiobookSleepTimerModes.Off, playback.SleepTimerState.Mode);
        Assert.Contains(nativeStates, state => state.Mode == AudiobookSleepTimerModes.Off
            && state.BoundAssetId == Guid.Empty
            && state.PlaybackRequestVersion == 0
            && state.TimerGeneration == invalidationGeneration);
        Assert.True(playback.SleepTimerRegistrationInProgress);

        candidateBinding.SetResult(true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => pendingSelection);

        Assert.False(playback.SleepTimerRegistrationInProgress);
        Assert.Equal(AudiobookSleepTimerModes.Off, playback.SleepTimerState.Mode);
        Assert.Equal(invalidationGeneration, playback.SleepTimerState.TimerGeneration);
        Assert.NotNull(pendingCandidate);
        Assert.False(playback.CanBindNativeSleepTimerState(pendingCandidate));
    }

    [Fact]
    public async Task ManualCrossAssetChapterProgressPreservesMinuteSleepTimer()
    {
        var profileId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var firstAsset = Guid.NewGuid();
        var secondAsset = Guid.NewGuid();
        var item = CreateAudiobookItem("Book", "stream://book") with
        {
            WorkId = workId,
            AudiobookWorkId = workId,
            AssetId = firstAsset,
            Chapters =
            [
                new() { Index = 0, AssetId = firstAsset, Title = "Chapter one", StartSeconds = 0, EndSeconds = 100 },
                new() { Index = 1, AssetId = secondAsset, Title = "Chapter two", StartSeconds = 0, EndSeconds = 200 },
            ],
        };
        var playback = new PlaybackSessionController(null!, null!, preferences: new ActiveProfilePlaybackPreferences(profileId));
        playback.RestoreState(new ListenPlaybackSnapshot
        {
            Queue = [item],
            CurrentIndex = 0,
            Experience = PlayerExperienceModes.Audiobook,
            CurrentTimeSeconds = 12,
            DurationSeconds = 100,
        });
        playback.SleepTimerNativeArmRequested += _ => Task.FromResult(true);
        var armed = await playback.SetAudiobookSleepTimerAsync(profileId, workId, firstAsset,
            playback.PlaybackRequestVersion,
            new AudiobookSleepTimerSelectionDto { Mode = AudiobookSleepTimerModes.Timer, Minutes = 15 });

        await playback.PlayAudiobookChapterAsync(item, item.Chapters[1]);

        Assert.Equal(AudiobookSleepTimerModes.Timer, playback.SleepTimerState.Mode);
        Assert.Equal(armed.TimerGeneration, playback.SleepTimerState.TimerGeneration);
        Assert.Equal(armed.DeadlineUtc, playback.SleepTimerState.DeadlineUtc);
        Assert.Equal(secondAsset, playback.SleepTimerState.BoundAssetId);
        Assert.Equal(playback.PlaybackRequestVersion, playback.SleepTimerState.PlaybackRequestVersion);
        await playback.SetAudiobookSleepTimerAsync(profileId, workId, secondAsset,
            playback.PlaybackRequestVersion, new AudiobookSleepTimerSelectionDto { Mode = AudiobookSleepTimerModes.Off });
    }

    [Fact]
    public async Task FailedNativeRearmKeepsPreviouslyConfirmedTimer()
    {
        var profileId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var item = CreateAudiobookItem("Book", "stream://book") with
        {
            WorkId = workId,
            AudiobookWorkId = workId,
            AssetId = assetId,
            Chapters = [new() { Index = 0, AssetId = assetId, Title = "Chapter one", StartSeconds = 0, EndSeconds = 1200 }],
        };
        var playback = new PlaybackSessionController(null!, null!, preferences: new ActiveProfilePlaybackPreferences(profileId));
        playback.RestoreState(new ListenPlaybackSnapshot
        {
            Queue = [item], CurrentIndex = 0, Experience = PlayerExperienceModes.Audiobook,
        });
        var rejectNextBinding = false;
        playback.SleepTimerNativeArmRequested += state => Task.FromResult(!rejectNextBinding);
        var armed = await playback.SetAudiobookSleepTimerAsync(profileId, workId, assetId,
            playback.PlaybackRequestVersion,
            new AudiobookSleepTimerSelectionDto { Mode = AudiobookSleepTimerModes.Timer, Minutes = 15 });

        rejectNextBinding = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => playback.SetAudiobookSleepTimerAsync(profileId, workId, assetId,
            playback.PlaybackRequestVersion,
            new AudiobookSleepTimerSelectionDto { Mode = AudiobookSleepTimerModes.Timer, Minutes = 30 }));

        Assert.Equal(armed, playback.SleepTimerState);
        rejectNextBinding = false;
        await playback.SetAudiobookSleepTimerAsync(profileId, workId, assetId,
            playback.PlaybackRequestVersion, new AudiobookSleepTimerSelectionDto { Mode = AudiobookSleepTimerModes.Off });
    }

    [Fact]
    public async Task ConcurrentTimerChoicesCommitInOrderWithoutEqualGenerationRaces()
    {
        var profileId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var item = CreateAudiobookItem("Book", "stream://book") with
        {
            WorkId = workId,
            AudiobookWorkId = workId,
            AssetId = assetId,
            Chapters = [new() { Index = 0, AssetId = assetId, Title = "Chapter one", StartSeconds = 0, EndSeconds = 1200 }],
        };
        var playback = new PlaybackSessionController(null!, null!, preferences: new ActiveProfilePlaybackPreferences(profileId));
        playback.RestoreState(new ListenPlaybackSnapshot
        {
            Queue = [item], CurrentIndex = 0, Experience = PlayerExperienceModes.Audiobook,
        });
        var firstBinding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bindings = new List<AudiobookSleepTimerStateDto>();
        playback.SleepTimerNativeArmRequested += state =>
        {
            bindings.Add(state);
            return bindings.Count == 1 ? firstBinding.Task.ContinueWith(_ => true) : Task.FromResult(true);
        };

        var first = playback.SetAudiobookSleepTimerAsync(profileId, workId, assetId,
            playback.PlaybackRequestVersion,
            new AudiobookSleepTimerSelectionDto { Mode = AudiobookSleepTimerModes.Timer, Minutes = 15 });
        var second = playback.SetAudiobookSleepTimerAsync(profileId, workId, assetId,
            playback.PlaybackRequestVersion,
            new AudiobookSleepTimerSelectionDto { Mode = AudiobookSleepTimerModes.Timer, Minutes = 30 });
        firstBinding.SetResult();
        var firstState = await first;
        var secondState = await second;

        Assert.True(secondState.TimerGeneration > firstState.TimerGeneration);
        Assert.Equal(30, secondState.ChosenMinutes);
        Assert.Equal(secondState, playback.SleepTimerState);
        Assert.Equal(2, bindings.Count);
        await playback.SetAudiobookSleepTimerAsync(profileId, workId, assetId,
            playback.PlaybackRequestVersion, new AudiobookSleepTimerSelectionDto { Mode = AudiobookSleepTimerModes.Off });
    }

    [Theory]
    [InlineData("/media/assets/312274cc-8cf0-4ead-9934-1aa78eb2b195/stream")]
    [InlineData("http://engine.test/media/assets/312274cc-8cf0-4ead-9934-1aa78eb2b195/stream")]
    public async Task ResourceStreamRoutesUseAuthenticatedDashboardProxy(string stream)
    {
        var service = new PlaybackSessionController(null!, null!);
        await service.PlayAudiobookAsync(CreateAudiobookItem("Recording", stream));
        Assert.Equal("/engine-stream/312274cc-8cf0-4ead-9934-1aa78eb2b195", service.CurrentBrowserStreamUrl);
    }

    [Fact]
    public async Task HeartbeatsUseServerSessionMonotonicSequenceAndExplicitEndFact()
    {
        var profileId = Guid.NewGuid();
        var serverSessionId = Guid.NewGuid();
        var handler = new TelemetryHeartbeatHandler(profileId, serverSessionId);
        var apiClient = new EngineApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://engine.test") },
            NullLogger<EngineApiClient>.Instance);
        using var profiles = new ActiveProfileSessionService(new NullJsRuntime(), apiClient);
        await using var orchestrator = new UIOrchestratorService(
            apiClient, new UniverseStateContainer(), profiles, new ConfigurationManager(),
            NullLogger<UIOrchestratorService>.Instance);
        var service = new PlaybackSessionController(orchestrator, apiClient);
        service.RestoreState(new ListenPlaybackSnapshot
        {
            Queue = [CreateAudiobookItem("Telemetry", "/stream/telemetry")],
            CurrentIndex = 0,
            CurrentTimeSeconds = 20,
            DurationSeconds = 60,
            IsPlaying = true,
        });

        await service.ReportHeartbeatAsync(force: true);
        await service.ReportHeartbeatAsync(force: true);
        await service.CompleteCurrentAsync();

        Assert.Equal([1, 2, 3], handler.Heartbeats.Select(value => value.Sequence));
        Assert.Null(handler.Heartbeats[0].SessionId);
        Assert.All(handler.Heartbeats.Skip(1), value => Assert.Equal(serverSessionId, value.SessionId));
        Assert.False(handler.Heartbeats[0].HasPlaybackEnded);
        Assert.True(handler.Heartbeats[^1].HasPlaybackEnded);
    }

    [Fact]
    public async Task PendingTransportCommands_CoalesceStateAndKeepDistinctUserActions()
    {
        var service = new PlaybackSessionController(null!, null!);
        var dispatched = new List<PlaybackTransportCommand>();
        service.TransportCommandRequested += command =>
        {
            dispatched.Add(command);
            return Task.CompletedTask;
        };

        service.SetTransportHostNotReady();
        await service.RequestTransportCommandAsync(new("seek", 10));
        await service.RequestTransportCommandAsync(new("seek", 20));
        await service.RequestTransportCommandAsync(new("set-volume", .25));
        await service.RequestTransportCommandAsync(new("set-volume", .75));
        await service.RequestTransportCommandAsync(new("toggle-play"));
        await service.RequestTransportCommandAsync(new("toggle-play"));

        await service.SetTransportHostReadyAsync();

        Assert.Equal(4, dispatched.Count);
        Assert.Single(dispatched, command => command.Action == "seek" && command.Value == 20);
        Assert.Single(dispatched, command => command.Action == "set-volume" && command.Value == .75);
        Assert.Equal(2, dispatched.Count(command => command.Action == "toggle-play"));
        Assert.Equal(dispatched.Count, dispatched.Select(command => command.RequestId).Distinct().Count());
    }

    [Fact]
    public async Task TransportCommand_WithSameRequestId_IsNotDispatchedTwice()
    {
        var service = new PlaybackSessionController(null!, null!);
        var dispatchCount = 0;
        service.TransportCommandRequested += _ =>
        {
            dispatchCount++;
            return Task.CompletedTask;
        };
        var reconnectReplay = new PlaybackTransportCommand("pause", RequestId: 9001);

        await service.RequestTransportCommandAsync(reconnectReplay);
        await service.RequestTransportCommandAsync(reconnectReplay);

        Assert.Equal(1, dispatchCount);
    }

    [Fact]
    public void CreateSnapshot_RoundTripsQueueHistoryAndTransportState()
    {
        var service = new PlaybackSessionController(null!, null!);
        var audiobookWorkId = Guid.NewGuid();
        var audiobookAssetId = Guid.NewGuid();
        var snapshot = new ListenPlaybackSnapshot
        {
            Queue =
            [
                CreateAudiobookItem("Current Book", "stream://current") with
                {
                    WorkId = audiobookWorkId,
                    AudiobookWorkId = audiobookWorkId,
                    AssetId = audiobookAssetId,
                },
                CreateQueueItem("Next Song", "stream://next"),
            ],
            History =
            [
                CreateQueueItem("Previous Song", "stream://previous") with { PlayedAt = new DateTimeOffset(2026, 4, 21, 12, 0, 0, TimeSpan.Zero) }
            ],
            CurrentIndex = 0,
            SourceLabel = "All Music",
            IsPanelOpen = true,
            ActiveTab = ListenPlaybackTabs.History,
            CurrentTimeSeconds = 42,
            DurationSeconds = 180,
            Volume = 0.55,
            IsMuted = true,
            PlaybackRate = 1.5d,
            Experience = PlayerExperienceModes.Audiobook,
            NeedsUserGestureToStart = true,
            IsPlaying = false,
            IsPopupOpen = true,
            AudiobookHistory =
            [
                new AudiobookListenHistoryItemDto
                {
                    Id = Guid.NewGuid(),
                    WorkId = audiobookWorkId,
                    AssetId = audiobookAssetId,
                    Title = "Previous audiobook position",
                    PositionSeconds = 1240,
                    ProgressPct = 27.5,
                    StartedAt = DateTimeOffset.UtcNow.AddMinutes(-3),
                    EndedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
                },
            ],
            PlaybackStartVersion = 7,
            SleepTimerState = new AudiobookSleepTimerStateDto(),
        };

        service.RestoreState(snapshot);

        var roundTrip = service.CreateSnapshot();

        Assert.Equal(2, roundTrip.Queue.Count);
        Assert.Single(roundTrip.History);
        Assert.Equal(ListenPlaybackTabs.History, roundTrip.ActiveTab);
        Assert.Equal(42, roundTrip.CurrentTimeSeconds);
        Assert.Equal(180, roundTrip.DurationSeconds);
        Assert.Equal(0d, roundTrip.Volume);
        Assert.Equal(0.55d, service.LastAudibleVolume);
        Assert.True(roundTrip.IsMuted);
        Assert.Equal(1.5d, roundTrip.PlaybackRate);
        Assert.Equal(PlayerExperienceModes.Audiobook, roundTrip.Experience);
        Assert.True(roundTrip.NeedsUserGestureToStart);
        Assert.False(roundTrip.IsPlaying);
        Assert.True(roundTrip.IsPopupOpen);
        Assert.Equal(7, roundTrip.PlaybackStartVersion);
        Assert.Single(roundTrip.AudiobookHistory);
        Assert.Equal(AudiobookSleepTimerModes.Off, roundTrip.SleepTimerState.Mode);
    }

    [Fact]
    public async Task PlayAudiobookAsync_UsesSingleItemModeAndAppliesResumeRewind()
    {
        var service = new PlaybackSessionController(null!, null!);
        var audiobook = CreateAudiobookItem("Dungeon Crawler Carl", "stream://dungeon-crawler-carl") with
        {
            InitialPositionSeconds = 123,
        };

        await service.PlayAudiobookAsync(audiobook, "Dungeon Crawler Carl");

        Assert.True(service.IsAudiobookMode);
        Assert.Single(service.Queue);
        Assert.Equal("Dungeon Crawler Carl", service.CurrentItem?.Title);
        Assert.Equal(113, service.CurrentTimeSeconds);
        Assert.Equal(1.25d, service.PlaybackRate);
        Assert.True(service.IsPlaying);
        Assert.False(service.NeedsUserGestureToStart);
    }

    [Fact]
    public async Task MusicHistory_ExcludesAudiobookWhenSwitchingBackToMusic()
    {
        var service = new PlaybackSessionController(null!, null!);

        await service.PlayQueueItemAsync(CreateQueueItem("First song", "stream://first-song"));
        await service.PlayAudiobookAsync(CreateAudiobookItem("Project Hail Mary", "stream://book"));
        await service.PlayQueueItemAsync(CreateQueueItem("Next song", "stream://next-song"));

        Assert.Equal(PlayerExperienceModes.Music, service.Experience);
        Assert.Contains(service.History, item => item.Title == "Project Hail Mary");
        Assert.Contains(service.History, item => item.Title == "First song");
        Assert.Single(service.MusicHistory);
        Assert.Equal("First song", service.MusicHistory[0].Title);
        Assert.Equal("Audiobooks", service.History.First(item => item.Title == "Project Hail Mary").MediaType);

        service.RestoreState(service.CreateSnapshot() with
        {
            History = service.History.Append(CreateQueueItem("Unknown legacy item", "stream://legacy") with { MediaType = "Unknown" }).ToList(),
        });
        Assert.DoesNotContain(service.MusicHistory, item => item.Title == "Unknown legacy item");
    }

    [Fact]
    public async Task PlayAudiobookAsync_StartsFromBeginningWhenAutomaticResumeIsDisabled()
    {
        var settings = UserPlaybackSettingsDto.CreateDefaults(Guid.NewGuid());
        settings.General.ResumePlayback = false;
        var service = new PlaybackSessionController(null!, null!, preferences: new PlaybackPreferencesStub(settings));
        var audiobook = CreateAudiobookItem("Dungeon Crawler Carl", "stream://dungeon-crawler-carl") with
        {
            InitialPositionSeconds = 123,
        };

        await service.PlayAudiobookAsync(audiobook, "Dungeon Crawler Carl");

        Assert.Equal(0, service.CurrentTimeSeconds);
    }

    [Fact]
    public async Task PlayAudiobookChapterAsync_UsesExactChapterStartWithoutResumeRewind()
    {
        var service = new PlaybackSessionController(null!, null!);
        var chapter = new PlaybackChapterDto
        {
            Index = 2,
            Title = "003",
            StartSeconds = 1256.245,
            EndSeconds = 2664.814,
        };
        var audiobook = CreateAudiobookItem("Dungeon Crawler Carl", "stream://dungeon-crawler-carl") with
        {
            InitialPositionSeconds = 6400,
            Chapters = [chapter],
        };

        await service.PlayAudiobookChapterAsync(audiobook, chapter, "Dungeon Crawler Carl");

        Assert.Equal(1256.245, service.CurrentTimeSeconds, 3);
        Assert.Equal(2, service.CurrentItem?.ChapterIndex);
        Assert.True(service.CurrentItem?.StartAtExactPosition);
        Assert.Equal("003", service.CurrentItem?.Subtitle);
    }

    [Fact]
    public async Task PlayAudiobookAsync_IncrementsPlaybackStartVersionForSameStreamStarts()
    {
        var service = new PlaybackSessionController(null!, null!);
        var audiobook = CreateAudiobookItem("Dungeon Crawler Carl", "stream://dungeon-crawler-carl");

        await service.PlayAudiobookAsync(audiobook, "Dungeon Crawler Carl");
        var firstVersion = service.PlaybackStartVersion;

        await service.PlayAudiobookAsync(audiobook with { InitialPositionSeconds = 1200 }, "Dungeon Crawler Carl");

        Assert.True(service.PlaybackStartVersion > firstVersion);
        Assert.Equal(1190, service.CurrentTimeSeconds);
    }

    [Fact]
    public async Task PlayAudiobookAsync_CreatesStartCommandWithBootstrappedStreamAndResumePosition()
    {
        var service = new PlaybackSessionController(null!, null!);
        var assetId = Guid.NewGuid();
        PlaybackTransportCommand? command = null;
        service.TransportCommandRequested += next =>
        {
            command = next;
            return Task.CompletedTask;
        };
        var audiobook = CreateAudiobookItem("Dungeon Crawler Carl", "stream://placeholder") with
        {
            AssetId = assetId,
            StreamUrl = null,
            InitialPositionSeconds = 123,
            Chapters =
            [
                new PlaybackChapterDto
                {
                    Index = 0,
                    Title = "Intro",
                    StartSeconds = 0,
                    EndSeconds = 15,
                },
                new PlaybackChapterDto
                {
                    Index = 1,
                    Title = "Chapter 1",
                    StartSeconds = 15,
                    EndSeconds = 1255,
                },
            ],
        };

        await service.PlayAudiobookAsync(audiobook, "Dungeon Crawler Carl");

        Assert.NotNull(command);
        Assert.Equal("start", command.Action);
        Assert.Equal($"/engine-stream/{assetId:D}", command.StreamUrl);
        Assert.Equal(113, command.PositionSeconds);
        Assert.Equal(1.25d, command.PlaybackRate);
        Assert.Equal(service.PlaybackStartVersion, command.RequestId);
        Assert.Equal(113, service.CurrentTimeSeconds);
    }

    [Fact]
    public async Task PlayAudiobookAsync_EmitsStartCommandBeforeManifestRefreshCompletes()
    {
        var assetId = Guid.NewGuid();
        var handler = new BlockingManifestHandler(assetId);
        var apiClient = new EngineApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://engine.test") },
            NullLogger<EngineApiClient>.Instance);
        var service = new PlaybackSessionController(null!, apiClient);
        var commandSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PlaybackTransportCommand? command = null;
        service.TransportCommandRequested += next =>
        {
            command = next;
            commandSeen.TrySetResult();
            return Task.CompletedTask;
        };
        var audiobook = CreateAudiobookItem("Dungeon Crawler Carl", "stream://placeholder") with
        {
            AssetId = assetId,
            StreamUrl = null,
            InitialPositionSeconds = 123,
        };

        var startTask = service.PlayAudiobookAsync(audiobook, "Dungeon Crawler Carl");
        var first = await Task.WhenAny(commandSeen.Task, Task.Delay(TimeSpan.FromSeconds(1)));

        Assert.Same(commandSeen.Task, first);
        Assert.False(startTask.IsCompleted);
        Assert.NotNull(command);
        Assert.Equal("start", command.Action);
        Assert.Equal($"/engine-stream/{assetId:D}", command.StreamUrl);
        Assert.Equal(113, command.PositionSeconds);

        handler.ReleaseManifest();
        await startTask;
    }

    [Fact]
    public async Task SetPlaybackRateAsync_SetsExactSelectedRate()
    {
        var service = new PlaybackSessionController(null!, null!);

        await service.SetPlaybackRateAsync(1.333d);

        Assert.Equal(1.333d, service.PlaybackRate);

        await service.SetPlaybackRateAsync(3.01d);
        Assert.Equal(1.333d, service.PlaybackRate);
        Assert.Contains("must be between", service.CurrentError ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        await service.SetPlaybackRateAsync(1.75d);
        Assert.Equal(1.75d, service.PlaybackRate);
        Assert.DoesNotContain("must be between", service.CurrentError ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RestoreState_BlocksPlaybackForUnsupportedRateAndRecoversOnExplicitChoice()
    {
        var service = new PlaybackSessionController(null!, null!);
        await service.SetPlaybackRateAsync(1.333d);
        var item = CreateAudiobookItem("Current book", "stream://book");
        service.RestoreState(new ListenPlaybackSnapshot
        {
            Queue = [item], CurrentIndex = 0, PlaybackRate = 8d, IsPlaying = true,
        });

        Assert.Equal(1.333d, service.PlaybackRate);
        Assert.False(service.IsPlaying);
        Assert.Contains("saved playback speed is unsupported", service.CurrentError ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        service.UpdateTransportState(isPlaying: true, playbackRate: 1.25d,
            expectedPlaybackRateSelectionVersion: service.PlaybackRateSelectionVersion);
        service.ApplyPlayerState(new PlayerStateDto { PlaybackRate = 1.5d }, service.PlaybackRateSelectionVersion);
        Assert.Equal(1.333d, service.PlaybackRate);
        Assert.False(service.IsPlaying);

        await service.SetPlaybackRateAsync(1.75d);
        Assert.Equal(1.75d, service.PlaybackRate);
        Assert.DoesNotContain("unsupported", service.CurrentError ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.False(service.IsPlaying);
    }

    [Fact]
    public async Task InvalidSavedDefaultDoesNotAutoplayAndExplicitValidRateRestoresPlayControl()
    {
        var settings = UserPlaybackSettingsDto.CreateDefaults(Guid.NewGuid());
        settings.Watching.DefaultPlaybackSpeed = 3.01m;
        var service = new PlaybackSessionController(null!, null!, preferences: new PlaybackPreferencesStub(settings));
        var actions = new List<string>();
        service.TransportCommandRequested += command =>
        {
            actions.Add(command.Action);
            return Task.CompletedTask;
        };

        await service.PlayVideoAsync(CreateVideoItem("Current movie", "stream://movie"));
        Assert.DoesNotContain("start", actions);
        Assert.False(service.IsPlaying);
        Assert.Contains("saved playback speed is unsupported", service.CurrentError ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        await service.SetPlaybackRateAsync(1.25d);
        Assert.Null(service.CurrentError);
        Assert.False(service.IsPlaying);
        await service.DispatchAsync(PlaybackCommand.TogglePlay());
        Assert.Contains("toggle-play", actions);
    }

    [Fact]
    public async Task VerifiedSameBookChapterKeepsExplicitRateWhenSavedDefaultIsInvalid()
    {
        var settings = UserPlaybackSettingsDto.CreateDefaults(Guid.NewGuid());
        settings.Listening.AudiobookDefaultSpeed = 3.01m;
        var service = new PlaybackSessionController(null!, null!, preferences: new PlaybackPreferencesStub(settings));
        var assetId = Guid.NewGuid();
        var book = CreateAudiobookItem("Recording", $"stream://{assetId:D}") with
        {
            AssetId = assetId,
            Chapters = [
                new() { Index = 0, AssetId = assetId, Title = "Chapter one", StartSeconds = 0, EndSeconds = 90 },
                new() { Index = 1, AssetId = assetId, Title = "Chapter two", StartSeconds = 90, EndSeconds = 180 },
            ],
        };

        await service.PlayAudiobookAsync(book);
        Assert.False(service.IsPlaying);
        Assert.Contains("saved playback speed is unsupported", service.CurrentError ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        await service.SetPlaybackRateAsync(1.75d);
        await service.PlayAudiobookChapterAsync(1);

        Assert.Equal(1.75d, service.PlaybackRate);
        Assert.True(service.IsPlaying);
        Assert.DoesNotContain("unsupported", service.CurrentError ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DelayedNativeRateObservationCannotUndoNewerChoiceButTimingAndVolumeStillApply()
    {
        var service = new PlaybackSessionController(null!, null!);
        var item = CreateAudiobookItem("Current book", "stream://book");
        service.RestoreState(new ListenPlaybackSnapshot { Queue = [item], CurrentIndex = 0, PlaybackRate = 1.333d });
        service.ReservePlaybackRequest();
        var requestVersion = service.PlaybackRequestVersion;
        var staleRateSelection = service.PlaybackRateSelectionVersion;

        await service.SetPlaybackRateAsync(1.75d);
        service.UpdateTransportState(playbackRate: 1.5d);
        Assert.Equal(1.75d, service.PlaybackRate);
        service.UpdateTransportState(
            currentTimeSeconds: 43,
            volume: 0.4d,
            playbackRate: 1.25d,
            expectedPlaybackRateSelectionVersion: staleRateSelection);

        Assert.Equal(1.75d, service.PlaybackRate);
        Assert.Equal(43d, service.CurrentTimeSeconds);
        Assert.Equal(0.4d, service.Volume);
        Assert.Equal(requestVersion, service.PlaybackRequestVersion);
    }

    [Fact]
    public void MutedRestoredSnapshotKeepsItsPositiveVolumeAsTheUnmuteTarget()
    {
        var service = new PlaybackSessionController(null!, null!);
        service.RestoreState(new ListenPlaybackSnapshot
        {
            Volume = 1d,
            IsMuted = true,
        });

        Assert.Equal(0d, service.Volume);
        Assert.Equal(1d, service.GetMuteToggleTargetVolume());

        service.RestoreState(new ListenPlaybackSnapshot { Volume = 0d });
        Assert.Equal(0d, service.Volume);
        Assert.True(service.GetMuteToggleTargetVolume() > 0d);
    }

    [Fact]
    public async Task QueuedMuteTogglesResolveInOrderAndZeroVolumeObservationsKeepRestoreTarget()
    {
        var service = new PlaybackSessionController(null!, null!);
        service.RestoreState(new ListenPlaybackSnapshot { Volume = 0.65d });

        service.SetTransportHostNotReady();
        var appliedVolumes = new List<double>();
        service.TransportCommandRequested += command =>
        {
            Assert.Equal("toggle-mute", command.Action);
            var targetVolume = service.GetMuteToggleTargetVolume();
            appliedVolumes.Add(targetVolume);
            service.UpdateTransportState(volume: targetVolume, isMuted: targetVolume <= 0d);
            return Task.CompletedTask;
        };

        await service.DispatchAsync(new PlaybackCommand(PlaybackCommandKind.ToggleMute));
        await service.DispatchAsync(new PlaybackCommand(PlaybackCommandKind.ToggleMute));
        Assert.Empty(appliedVolumes);

        await service.SetTransportHostReadyAsync();

        Assert.Equal(new[] { 0d, 0.65d }, appliedVolumes);
        Assert.Equal(0.65d, service.Volume);
        Assert.False(service.IsMuted);
        Assert.Equal(0.65d, service.LastAudibleVolume);

        service.UpdateTransportState(volume: 0d, isMuted: true);
        Assert.Equal(0.65d, service.LastAudibleVolume);
        service.UpdateTransportState(volume: 0.37d, isMuted: true);
        Assert.Equal(0.37d, service.LastAudibleVolume);
        Assert.Equal(0.37d, service.GetMuteToggleTargetVolume());
        service.UpdateTransportState(volume: 0.37d, isMuted: false);
        Assert.Equal(0d, service.GetMuteToggleTargetVolume());
    }

    [Fact]
    public async Task LateHeartbeatRateCannotReplaceNewerUserSelection()
    {
        var service = new PlaybackSessionController(null!, null!);
        service.RestoreState(new ListenPlaybackSnapshot { PlaybackRate = 1.333d });
        var staleSelectionVersion = 1L;

        await service.SetPlaybackRateAsync(1.75d);
        service.ApplyPlayerState(new PlayerStateDto { PlaybackRate = 1.25d }, staleSelectionVersion);

        Assert.Equal(1.75d, service.PlaybackRate);
    }

    [Fact]
    public async Task PopupSetSpeed_RequiresCurrentProfileWorkAssetAndRequestVersion()
    {
        var profileId = Guid.NewGuid();
        var item = CreateAudiobookItem("Current book", "stream://book");
        var playback = new PlaybackSessionController(null!, null!);
        playback.RestoreState(new ListenPlaybackSnapshot { Queue = [item], CurrentIndex = 0, PlaybackRate = 1.25d });
        playback.ReservePlaybackRequest();
        using var services = new ServiceCollection()
            .AddSingleton<IUserPlaybackPreferencesAccessor>(new ActiveProfilePlaybackPreferences(profileId))
            .BuildServiceProvider();
        var owner = new ListenPlaybackCommandOwner(services, playback);
        var command = new ListenPlaybackCommandDto
        {
            CommandId = Guid.NewGuid(), SenderId = Guid.NewGuid(), RecipientId = owner.RecipientId,
            Action = ListenPlaybackCommandActions.SetSpeed, ProfileId = profileId, WorkId = item.WorkId,
            ExpectedAssetId = item.AssetId, ExpectedPlaybackRequestVersion = playback.PlaybackRequestVersion,
            Value = 1.75d,
        };

        var success = await owner.HandleAsync(command);
        Assert.Equal(AudiobookBookmarkOperationOutcomes.Success, success?.Outcome);
        Assert.Equal(1.75d, playback.PlaybackRate);

        var stale = command with { CommandId = Guid.NewGuid(), Value = 2.25d, ExpectedPlaybackRequestVersion = playback.PlaybackRequestVersion - 1 };
        var rejected = await owner.HandleAsync(stale);
        Assert.Equal(AudiobookBookmarkOperationOutcomes.DefiniteFailure, rejected?.Outcome);

        foreach (var invalidScope in new[]
        {
            command with { CommandId = Guid.NewGuid(), ProfileId = Guid.NewGuid() },
            command with { CommandId = Guid.NewGuid(), WorkId = Guid.NewGuid() },
            command with { CommandId = Guid.NewGuid(), ExpectedAssetId = Guid.NewGuid() },
            command with { CommandId = Guid.NewGuid(), Value = 3.01d },
        })
        {
            var scopeRejected = await owner.HandleAsync(invalidScope);
            Assert.Equal(AudiobookBookmarkOperationOutcomes.DefiniteFailure, scopeRejected?.Outcome);
        }

        Assert.Equal(1.75d, playback.PlaybackRate);
    }

    [Fact]
    public void NormalizeChapter_PreservesEmbeddedNumericTrackTitles()
    {
        Assert.Equal("001", PlaybackSessionController.NormalizeChapter(new PlaybackChapterDto { Title = "001" }, 0).Title);
        Assert.Equal("Dedication", PlaybackSessionController.NormalizeChapter(new PlaybackChapterDto { Title = "Dedication" }, 1).Title);
        Assert.Equal("Track 3", PlaybackSessionController.NormalizeChapter(new PlaybackChapterDto(), 2).Title);
    }

    [Fact]
    public async Task AddQueueItemAsync_Audiobook_ReplacesMusicQueueInsteadOfAppending()
    {
        var service = new PlaybackSessionController(null!, null!);
        await service.AddQueueItemAsync(CreateQueueItem("Current Song", "stream://song"));

        await service.AddQueueItemAsync(CreateAudiobookItem("Dungeon Crawler Carl", "stream://book"));

        Assert.True(service.IsAudiobookMode);
        Assert.Single(service.Queue);
        Assert.Equal("Dungeon Crawler Carl", service.Queue[0].Title);
        Assert.Single(service.History);
        Assert.Equal("Current Song", service.History[0].Title);
    }

    [Fact]
    public async Task PlayVideoAsync_UsesSharedSessionAndExpandsPersistentVideo()
    {
        var service = new PlaybackSessionController(null!, null!);
        PlaybackTransportCommand? command = null;
        service.TransportCommandRequested += next =>
        {
            command = next;
            return Task.CompletedTask;
        };
        var video = CreateVideoItem("Inception", "stream://inception") with
        {
            InitialPositionSeconds = 3272,
            Quality = "1080p",
        };

        await service.PlayVideoAsync(video, "Inception");

        Assert.True(service.IsVideoMode);
        Assert.True(service.IsVideoExpanded);
        Assert.False(service.IsMusicMode);
        Assert.False(service.IsAudiobookMode);
        Assert.Single(service.Queue);
        Assert.Equal(3272, service.CurrentTimeSeconds);
        Assert.Equal(10, service.SkipBackSeconds);
        Assert.Equal(30, service.SkipForwardSeconds);
        Assert.Equal("1080p", service.CurrentItem?.Quality);
        Assert.Equal("start", command?.Action);
        Assert.Equal("stream://inception", command?.StreamUrl);
    }

    [Fact]
    public async Task ViewSession_UsesLocalAssetIdentityAndNeverEntersCatalogueQueue()
    {
        var service = new PlaybackSessionController(null!, null!);
        var firstAsset = Guid.NewGuid();
        var nextAsset = Guid.NewGuid();

        await service.BeginViewSessionAsync(firstAsset, ViewPlaybackKind.Video);
        service.UpdateViewSession(firstAsset, new AudioTransportState(
            CurrentTimeSeconds: 42, DurationSeconds: 180, IsPlaying: true,
            Volume: .5, PlaybackRate: 1.25), PlaybackPresentationSurface.PictureInPicture);

        Assert.Empty(service.State.Queue);
        Assert.False(service.State.HasQueue);
        Assert.Equal(firstAsset, service.State.ViewSession?.AssetId);
        Assert.Equal(42, service.State.ViewSession?.PositionSeconds);
        Assert.Equal(PlaybackPresentationSurface.PictureInPicture, service.State.ViewSession?.PresentationSurface);

        await service.BeginViewSessionAsync(nextAsset, ViewPlaybackKind.Video);
        service.UpdateViewSession(firstAsset, new AudioTransportState(CurrentTimeSeconds: 99));
        service.EndViewSession(firstAsset);
        Assert.Equal(nextAsset, service.ViewSession?.AssetId);
        Assert.Equal(0, service.ViewSession?.PositionSeconds);

        await service.PlayVideoAsync(CreateVideoItem("Film", "stream://film"));
        Assert.Null(service.State.ViewSession);
        Assert.Single(service.State.Queue);
    }

    [Fact]
    public async Task OpeningViewVideo_PausesAndClosesAnActiveCatalogueVideo()
    {
        var service = new PlaybackSessionController(null!, null!);
        var commands = new List<PlaybackTransportCommand>();
        service.TransportCommandRequested += command =>
        {
            commands.Add(command);
            return Task.CompletedTask;
        };

        await service.PlayVideoAsync(CreateVideoItem("Film", "stream://film"));
        await service.BeginViewSessionAsync(Guid.NewGuid(), ViewPlaybackKind.Video);

        Assert.Equal(["start", "pause"], commands.Select(command => command.Action));
        Assert.True(commands[1].RequestId > commands[0].RequestId);
        Assert.False(service.HasQueue);
        Assert.False(service.IsPlaying);
        Assert.NotNull(service.ViewSession);
    }

    [Fact]
    public async Task ChangingPresentationSurface_DoesNotRestartOrReplaceTheSession()
    {
        var service = new PlaybackSessionController(null!, null!);
        var song = CreateQueueItem("Track", "stream://track");
        await service.AddQueueItemAsync(song);
        service.UpdateTransportState(currentTimeSeconds: 42, isPlaying: true);
        var version = service.PlaybackStartVersion;

        service.SetPresentationSurface(PlaybackPresentationSurface.NowPlaying);
        Assert.Equal(PlaybackPresentationSurface.NowPlaying, service.State.PresentationSurface);
        service.SetPresentationSurface(PlaybackPresentationSurface.Docked);
        Assert.Equal(song.WorkId, service.CurrentItem?.WorkId);
        Assert.Equal(42, service.CurrentTimeSeconds);
        Assert.True(service.IsPlaying);
        Assert.Equal(version, service.PlaybackStartVersion);

        var video = CreateVideoItem("Film", "stream://film");
        await service.PlayVideoAsync(video);
        version = service.PlaybackStartVersion;
        service.SetVideoExpanded(false);
        Assert.Equal(PlaybackPresentationSurface.PictureInPicture, service.State.PresentationSurface);
        service.SetPresentationSurface(PlaybackPresentationSurface.RestorableVideo);
        Assert.Equal(PlaybackPresentationSurface.RestorableVideo, service.State.PresentationSurface);
        Assert.False(service.IsVideoExpanded);
        service.SetVideoExpanded(true);
        Assert.Equal(PlaybackPresentationSurface.PrimaryVideo, service.State.PresentationSurface);
        Assert.Equal(video.WorkId, service.CurrentItem?.WorkId);
        Assert.Equal(version, service.PlaybackStartVersion);
    }

    [Fact]
    public async Task ReplacingVideoQueue_OpensPrimaryVideoWithoutDroppingOwnedNextItem()
    {
        var service = new PlaybackSessionController(null!, null!);
        var first = CreateVideoItem("Episode 1", "stream://episode-one");
        var next = CreateVideoItem("Episode 2", "stream://episode-two");

        await service.ReplaceQueueItemsAsync([first, next], 0, "Series", shuffle: false);

        Assert.Equal(PlaybackPresentationSurface.PrimaryVideo, service.PresentationSurface);
        Assert.True(service.IsVideoExpanded);
        Assert.Equal([first.WorkId, next.WorkId], service.Queue.Select(item => item.WorkId));
    }

    [Fact]
    public async Task AppendVideoNextUpAsync_PreservesActivePlaybackAndRejectsStaleLookup()
    {
        var service = new PlaybackSessionController(null!, null!);
        var current = CreateVideoItem("Episode 1", "stream://episode-one");
        var next = CreateVideoItem("Episode 2", "stream://episode-two");
        await service.PlayVideoAsync(current, "Series");
        var requestVersion = service.PlaybackRequestVersion;
        var startVersion = service.PlaybackStartVersion;

        Assert.True(await service.AppendVideoNextUpAsync(next, current.WorkId, requestVersion));
        Assert.Equal([current.WorkId, next.WorkId], service.Queue.Select(item => item.WorkId));
        Assert.Equal(current.WorkId, service.CurrentItem?.WorkId);
        Assert.Equal(startVersion, service.PlaybackStartVersion);
        Assert.False(await service.AppendVideoNextUpAsync(next, current.WorkId, requestVersion));
        Assert.False(await service.AppendVideoNextUpAsync(
            CreateVideoItem("Episode 3", "stream://episode-three"), current.WorkId, requestVersion - 1));
        Assert.Equal(2, service.Queue.Count);
    }

    [Fact]
    public async Task PlayVideoAsync_UsesSignedHlsUrlWhenManifestRequiresAdaptiveDelivery()
    {
        var service = new PlaybackSessionController(null!, null!);
        PlaybackTransportCommand? command = null;
        service.TransportCommandRequested += next =>
        {
            command = next;
            return Task.CompletedTask;
        };
        var packageId = Guid.NewGuid();
        var video = CreateVideoItem("Inception", "/stream/source") with
        {
            Manifest = new PlaybackManifestDto
            {
                RecommendedDelivery = PlaybackDeliveryModes.Hls,
                DirectPlaySupported = false,
                DirectStreamUrl = "/stream/source",
                HlsUrl = $"/stream/hls/grant/{packageId:D}/master.m3u8",
            },
        };

        await service.PlayVideoAsync(video, "Inception");

        Assert.Equal($"/engine-hls/grant/{packageId:D}/master.m3u8", command?.StreamUrl);
        Assert.Equal($"/engine-hls/grant/{packageId:D}/master.m3u8", service.CurrentBrowserStreamUrl);
    }

    [Fact]
    public async Task AddQueueItemAsync_Video_ReplacesMusicQueueInsteadOfAppending()
    {
        var service = new PlaybackSessionController(null!, null!);
        await service.AddQueueItemAsync(CreateQueueItem("Current Song", "stream://song"));

        await service.AddQueueItemAsync(CreateVideoItem("Inception", "stream://inception"));

        Assert.True(service.IsVideoMode);
        Assert.Single(service.Queue);
        Assert.Equal("Inception", service.CurrentItem?.Title);
        Assert.Single(service.History);
        Assert.Equal("Current Song", service.History[0].Title);
    }

    [Fact]
    public void RestoreState_RoundTripsExpandedVideoExperience()
    {
        var service = new PlaybackSessionController(null!, null!);
        service.RestoreState(new ListenPlaybackSnapshot
        {
            Queue = [CreateVideoItem("The Expanse", "stream://episode")],
            CurrentIndex = 0,
            Experience = PlayerExperienceModes.Video,
            IsVideoExpanded = true,
            IsPlaying = true,
        });

        var snapshot = service.CreateSnapshot();

        Assert.True(service.IsVideoMode);
        Assert.True(service.IsVideoExpanded);
        Assert.Equal(PlayerExperienceModes.Video, snapshot.Experience);
        Assert.True(snapshot.IsVideoExpanded);
    }

    [Fact]
    public async Task PlayAudiobookAsync_NormalizesRelativeStreamUrlToEngineUrl()
    {
        var apiClient = new EngineApiClient(
            new HttpClient(new PlayerSyncHandler()) { BaseAddress = new Uri("http://engine.test") },
            NullLogger<EngineApiClient>.Instance);
        var service = new PlaybackSessionController(null!, apiClient);
        var audiobook = CreateAudiobookItem("Dungeon Crawler Carl", "/stream/312274cc-8cf0-4ead-9934-1aa78eb2b195");

        await service.PlayAudiobookAsync(audiobook, "Dungeon Crawler Carl");

        Assert.Equal("http://engine.test/stream/312274cc-8cf0-4ead-9934-1aa78eb2b195", service.CurrentStreamUrl);
    }

    [Fact]
    public async Task CurrentBrowserStreamUrl_UsesDashboardProxyForEngineDirectStreams()
    {
        var apiClient = new EngineApiClient(
            new HttpClient(new PlayerSyncHandler()) { BaseAddress = new Uri("http://engine.test") },
            NullLogger<EngineApiClient>.Instance);
        var service = new PlaybackSessionController(null!, apiClient);
        var audiobook = CreateAudiobookItem("Dungeon Crawler Carl", "/stream/312274cc-8cf0-4ead-9934-1aa78eb2b195");

        await service.PlayAudiobookAsync(audiobook, "Dungeon Crawler Carl");

        Assert.Equal("http://engine.test/stream/312274cc-8cf0-4ead-9934-1aa78eb2b195", service.CurrentStreamUrl);
        Assert.Equal("/engine-stream/312274cc-8cf0-4ead-9934-1aa78eb2b195", service.CurrentBrowserStreamUrl);
    }

    [Fact]
    public void ClearUpcoming_RemovesOnlyFutureQueueItems()
    {
        var service = new PlaybackSessionController(null!, null!);
        service.RestoreState(new ListenPlaybackSnapshot
        {
            Queue =
            [
                CreateQueueItem("Current", "stream://current"),
                CreateQueueItem("Upcoming One", "stream://one"),
                CreateQueueItem("Upcoming Two", "stream://two"),
            ],
            CurrentIndex = 0,
        });

        service.ClearUpcoming();

        Assert.Single(service.Queue);
        Assert.Equal("Current", service.Queue[0].Title);
    }

    [Fact]
    public void RemoveUpcomingAt_DoesNotRemoveCurrentItem()
    {
        var service = new PlaybackSessionController(null!, null!);
        service.RestoreState(new ListenPlaybackSnapshot
        {
            Queue =
            [
                CreateQueueItem("Current", "stream://current"),
                CreateQueueItem("Upcoming", "stream://upcoming"),
            ],
            CurrentIndex = 0,
        });

        service.RemoveUpcomingAt(0);

        Assert.Equal(2, service.Queue.Count);

        service.RemoveUpcomingAt(1);

        Assert.Single(service.Queue);
        Assert.Equal("Current", service.Queue[0].Title);
    }

    [Fact]
    public void ClosePlayer_ClearsQueueAndHistory()
    {
        var service = new PlaybackSessionController(null!, null!);
        service.RestoreState(new ListenPlaybackSnapshot
        {
            Queue = [CreateQueueItem("Current", "stream://current")],
            History = [CreateQueueItem("Previous", "stream://previous")],
            CurrentIndex = 0,
            IsPlaying = true,
            IsPopupOpen = true,
        });

        service.ClosePlayer();

        Assert.Empty(service.Queue);
        Assert.Empty(service.History);
        Assert.False(service.HasQueue);
        Assert.False(service.IsPopupOpen);
        Assert.True(service.IsDismissed);
    }

    [Fact]
    public void RestoreState_UsesQueuedItemKindWhenStoredExperienceDisagrees()
    {
        var service = new PlaybackSessionController(null!, null!);
        var song = CreateQueueItem("Beautiful", "stream://beautiful");
        service.RestoreState(new ListenPlaybackSnapshot
        {
            Queue = [song],
            CurrentIndex = 0,
            Experience = PlayerExperienceModes.Video,
            IsVideoExpanded = false,
            IsPlaying = true,
        });

        Assert.True(service.IsMusicMode);
        Assert.False(service.IsVideoMode);
        Assert.Equal(PlaybackPresentationSurface.Docked, service.PresentationSurface);
        Assert.Equal(song.WorkId, service.CurrentItem?.WorkId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelayedManifestCannotWriteIntoClosedOrReplacedQueue(bool replace)
    {
        var handler = new DelayedManifestHandler();
        var api = new EngineApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://engine.test") }, NullLogger<EngineApiClient>.Instance);
        var service = new PlaybackSessionController(null!, api);
        var pending = CreateVideoItem("Old video", "/stream/old") with
        {
            Manifest = new PlaybackManifestDto { HlsStatus = "preparing", RecommendedDelivery = PlaybackDeliveryModes.Hls },
        };
        var start = service.PlayVideoAsync(pending);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.ClosePlayer();
        var replacement = CreateQueueItem("New music", "stream://new");
        if (replace)
        {
            await service.PlayQueueItemAsync(replacement);
        }
        handler.Response.SetResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new PlaybackManifestDto { HlsStatus = "streaming", RecommendedDelivery = PlaybackDeliveryModes.Hls, HlsUrl = "/stream/hls/old/master.m3u8" }),
        });
        await start.WaitAsync(TimeSpan.FromSeconds(5));
        if (replace)
        {
            Assert.Equal(replacement.WorkId, service.CurrentItem!.WorkId);
        }
        else
        {
            Assert.Empty(service.Queue);
        }
        Assert.False(service.IsVideoMode);
    }

    [Fact]
    public async Task InitialBrowserSnapshotCannotReplaceExplicitSelection()
    {
        var service = new PlaybackSessionController(null!, null!);
        var video = CreateVideoItem("Selected episode", "stream://episode");
        await service.PlayVideoAsync(video);
        service.RestoreInitialState(new ListenPlaybackSnapshot { Queue = [CreateQueueItem("Old song", "stream://old")], CurrentIndex = 0 });
        Assert.Equal(video.WorkId, service.CurrentItem!.WorkId);
        Assert.True(service.IsVideoMode);
    }

    [Fact]
    public async Task SubjectProjectionPublishesBeforeSettingsAndRejectsStaleSettingsAndTransport()
    {
        var delayedSettings = UserPlaybackSettingsDto.CreateDefaults(Guid.Empty);
        delayedSettings.Watching.DefaultPlaybackSpeed = 2.4m;
        var preferences = new DelayedFirstPlaybackPreferences(delayedSettings);
        var service = new PlaybackSessionController(null!, null!, preferences: preferences);
        service.RestoreState(new ListenPlaybackSnapshot
        {
            Queue = [CreateQueueItem("Old song", "stream://old")],
            CurrentIndex = 0,
            IsPanelOpen = true,
            ActiveTab = ListenPlaybackTabs.Lyrics,
        });

        PlaybackSessionState? firstVideoProjection = null;
        service.Changed += _ =>
        {
            if (service.IsVideoMode && firstVideoProjection is null)
            {
                firstVideoProjection = service.State;
            }
        };
        var oldRequest = service.PlayVideoAsync(CreateVideoItem("Old video", "stream://old-video"));
        await preferences.FirstCallEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(firstVideoProjection);
        Assert.Equal("Old video", firstVideoProjection.CurrentItem?.Title);
        Assert.Equal(PlaybackPresentationSurface.PrimaryVideo, firstVideoProjection.PresentationSurface);
        Assert.False(firstVideoProjection.IsPanelOpen);
        Assert.Equal(ListenPlaybackTabs.Queue, firstVideoProjection.ActiveTab);

        PlaybackTransportCommand? dispatched = null;
        service.TransportCommandRequested += command =>
        {
            dispatched = command;
            return Task.CompletedTask;
        };
        var currentBook = CreateAudiobookItem("Current audiobook", "stream://book");
        await service.PlayAudiobookAsync(currentBook);
        var currentRate = service.PlaybackRate;
        preferences.ReleaseFirstCall();
        await oldRequest.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(currentBook.WorkId, service.CurrentItem?.WorkId);
        Assert.True(service.IsAudiobookMode);
        Assert.Equal(currentRate, service.PlaybackRate);
        Assert.DoesNotContain("old-video", dispatched?.StreamUrl ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolvedAssetCanUpdateARequestWhoseSourceHadNoAssetId()
    {
        var workId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var handler = new PlaybackAssetResolutionHandler(workId, assetId);
        var apiClient = new EngineApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://engine.test") },
            NullLogger<EngineApiClient>.Instance);
        using var profiles = new ActiveProfileSessionService(new NullJsRuntime(), apiClient);
        await using var orchestrator = new UIOrchestratorService(
            apiClient, new UniverseStateContainer(), profiles, new ConfigurationManager(),
            NullLogger<UIOrchestratorService>.Instance);
        var service = new PlaybackSessionController(orchestrator, apiClient);
        PlaybackTransportCommand? startCommand = null;
        service.TransportCommandRequested += command =>
        {
            if (command.Action == "start")
            {
                startCommand = command;
            }
            return Task.CompletedTask;
        };

        await service.PlayQueueItemAsync(new ListenQueueItem
        {
            WorkId = workId,
            MediaType = "Music",
            Title = "Resolved track",
        });

        Assert.Equal(workId, service.CurrentItem?.WorkId);
        Assert.Equal(assetId, service.CurrentItem?.AssetId);
        Assert.Equal($"/engine-stream/{assetId:D}", service.CurrentBrowserStreamUrl);
        Assert.Equal($"/engine-stream/{assetId:D}", startCommand?.StreamUrl);
    }

    [Fact]
    public void PlaybackIdentityRoutesUseExplicitIdsAndSurviveSnapshots()
    {
        var workId = Guid.NewGuid();
        var albumId = Guid.NewGuid();
        var artistId = Guid.NewGuid();
        var playlistId = Guid.NewGuid();
        var song = CreateQueueItem("Song title", "stream://song") with
        {
            WorkId = workId,
            AlbumWorkId = albumId,
            ArtistPersonId = artistId,
            PlaylistId = playlistId,
        };
        var original = new PlaybackSessionController(null!, null!);
        original.RestoreState(new ListenPlaybackSnapshot { Queue = [song], CurrentIndex = 0 });
        var restored = new PlaybackSessionController(null!, null!);
        restored.RestoreState(original.CreateSnapshot());

        Assert.Equal($"/details/musicalbum/{albumId:D}?context=listen", ListenPlaybackIdentityRoutes.Album(restored.CurrentItem));
        Assert.Equal($"/details/person/{artistId:D}", ListenPlaybackIdentityRoutes.Artist(restored.CurrentItem));
        Assert.Equal($"/listen/music/playlists/{playlistId:D}", ListenPlaybackIdentityRoutes.Playlist(restored.CurrentItem));
        Assert.Null(ListenPlaybackIdentityRoutes.Audiobook(restored.CurrentItem));
        Assert.Null(ListenPlaybackIdentityRoutes.Album(CreateQueueItem("Unresolved", "stream://unresolved")));

        var work = new WorkViewModel
        {
            Id = workId,
            RootWorkId = albumId,
            ArtistPersonId = artistId,
            CanonicalValues =
            [
                new CanonicalValueViewModel { Key = "artist", Value = "Primary Artist" },
                new CanonicalValueViewModel { Key = "album", Value = "Album Title" },
            ],
            MediaType = "Music",
        };
        var fromSource = ListenQueueItemFactory.Create(work);
        Assert.Equal(albumId, fromSource.AlbumWorkId);
        Assert.Equal(artistId, fromSource.ArtistPersonId);
        Assert.Equal("Primary Artist", fromSource.Subtitle);
        Assert.Equal("Album Title", fromSource.Album);
        Assert.Equal($"/details/musicalbum/{albumId:D}?context=listen", ListenPlaybackIdentityRoutes.Album(fromSource));
        Assert.Equal($"/details/person/{artistId:D}", ListenPlaybackIdentityRoutes.Artist(fromSource));

        var missingArtistName = ListenQueueItemFactory.Create(new WorkViewModel
        {
            Id = workId,
            RootWorkId = albumId,
            ArtistPersonId = artistId,
            CanonicalValues = [new CanonicalValueViewModel { Key = "album", Value = "Album Title" }],
            MediaType = "Music",
        });
        Assert.Null(missingArtistName.ArtistPersonId);
        Assert.Null(missingArtistName.Subtitle);
        Assert.Equal("Album Title", missingArtistName.Album);

        var audiobook = ListenQueueItemFactory.Create(new WorkViewModel
        {
            Id = workId,
            RootWorkId = albumId,
            MediaType = "Audiobook",
        });
        Assert.Equal(albumId, audiobook.AudiobookWorkId);
        Assert.Equal($"/details/audiobook/{albumId:D}?context=listen", ListenPlaybackIdentityRoutes.Audiobook(audiobook));

        var audiobookWithoutParent = ListenQueueItemFactory.Create(new WorkViewModel
        {
            Id = workId,
            MediaType = "Audiobook",
        });
        Assert.Equal(workId, audiobookWithoutParent.AudiobookWorkId);
    }

    [Theory]
    [InlineData("Movie", PlayerExperienceModes.Video, PlayerExperienceModes.Music)]
    [InlineData("Music", PlayerExperienceModes.Music, PlayerExperienceModes.Audiobook)]
    public void TypedCurrentSubjectWinsOverStaleBackendExperience(string mediaType, string currentExperience, string backendExperience)
    {
        var subject = CreateQueueItem("Current subject", "stream://current") with
        {
            WorkId = Guid.NewGuid(),
            MediaType = mediaType,
        };
        var playback = new PlaybackSessionController(null!, null!);
        playback.RestoreState(new ListenPlaybackSnapshot
        {
            Queue = [subject],
            CurrentIndex = 0,
            Experience = currentExperience,
        });

        playback.ApplyPlayerState(new PlayerStateDto { Experience = backendExperience });

        Assert.Equal(currentExperience, playback.Experience);
        Assert.Equal(subject.WorkId, playback.CurrentItem?.WorkId);
    }

    [Fact]
    public void UnknownCurrentSubjectCanUseBackendExperience()
    {
        var subject = CreateQueueItem("Unclassified audio", "stream://unknown") with
        {
            WorkId = Guid.NewGuid(),
            MediaType = string.Empty,
        };
        var playback = new PlaybackSessionController(null!, null!);
        playback.RestoreState(new ListenPlaybackSnapshot { Queue = [subject], CurrentIndex = 0 });

        playback.ApplyPlayerState(new PlayerStateDto { Experience = PlayerExperienceModes.Audiobook });

        Assert.Equal(PlayerExperienceModes.Audiobook, playback.Experience);
    }

    [Fact]
    public void AudiobookHistoryRejectsUnrelatedWorkAndUnknownAssets()
    {
        var bookId = Guid.NewGuid();
        var chapterWorkId = Guid.NewGuid();
        var chapterAssetId = Guid.NewGuid();
        var foreignTvWorkId = Guid.NewGuid();
        var foreignTvAssetId = Guid.NewGuid();
        var chapter = CreateAudiobookItem("Chapter one", "stream://chapter") with
        {
            WorkId = chapterWorkId,
            AudiobookWorkId = bookId,
            AssetId = chapterAssetId,
            Chapters = [new PlaybackChapterDto { Index = 0, AssetId = chapterAssetId }],
        };
        var playback = new PlaybackSessionController(null!, null!);
        playback.RestoreState(new ListenPlaybackSnapshot
        {
            Queue = [chapter],
            CurrentIndex = 0,
            Experience = PlayerExperienceModes.Audiobook,
            AudiobookHistory =
            [
                new AudiobookListenHistoryItemDto { Id = Guid.NewGuid(), WorkId = bookId, AssetId = chapterAssetId, Title = "Book chapter" },
                new AudiobookListenHistoryItemDto { Id = Guid.NewGuid(), WorkId = foreignTvWorkId, AssetId = foreignTvAssetId, Title = "TV episode" },
                new AudiobookListenHistoryItemDto { Id = Guid.NewGuid(), WorkId = bookId, AssetId = foreignTvAssetId, Title = "Foreign asset on book work" },
                new AudiobookListenHistoryItemDto { Id = Guid.NewGuid(), WorkId = foreignTvWorkId, AssetId = chapterAssetId, Title = "Foreign work on book asset" },
            ],
        });

        var validHistory = Assert.Single(playback.AudiobookHistory);
        playback.ApplyPlayerState(new PlayerStateDto
        {
            Experience = PlayerExperienceModes.Audiobook,
            AudiobookHistory =
            [
                validHistory,
                new AudiobookListenHistoryItemDto { Id = Guid.NewGuid(), WorkId = foreignTvWorkId, AssetId = foreignTvAssetId, Title = "Late TV episode" },
                new AudiobookListenHistoryItemDto { Id = Guid.NewGuid(), WorkId = bookId, AssetId = foreignTvAssetId, Title = "Late foreign asset" },
            ],
        });

        Assert.Equal("Chapter one", Assert.Single(playback.AudiobookHistory).Title);
    }

    [Fact]
    public void AudiobookHistoryUsesCanonicalChapterForVerifiedAssetInsteadOfStaleHistoricalTitle()
    {
        var bookId = Guid.NewGuid();
        var bookAssetId = Guid.NewGuid();
        var foreignWorkId = Guid.NewGuid();
        var foreignAssetId = Guid.NewGuid();
        var book = CreateAudiobookItem("Project Hail Mary", "stream://book") with
        {
            WorkId = bookId,
            AudiobookWorkId = bookId,
            AssetId = bookAssetId,
            Chapters =
            [
                new PlaybackChapterDto
                {
                    Index = 0,
                    AssetId = bookAssetId,
                    Title = "Chapter One (Override)",
                    TitleSource = PlaybackChapterTitleSources.Override,
                    StartSeconds = 0,
                    EndSeconds = 900,
                },
            ],
        };
        var staleBookHistory = new AudiobookListenHistoryItemDto
        {
            Id = Guid.NewGuid(),
            WorkId = bookId,
            AssetId = bookAssetId,
            Title = "Project Hail Mary",
            ChapterTitle = "I'm Used to It",
            ChapterIndex = -1,
            PositionSeconds = 34,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-20),
            EndedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
        };
        var playback = new PlaybackSessionController(null!, null!);
        playback.RestoreState(new ListenPlaybackSnapshot
        {
            Queue = [book],
            CurrentIndex = 0,
            Experience = PlayerExperienceModes.Audiobook,
            AudiobookHistory =
            [
                staleBookHistory,
                staleBookHistory with { Id = Guid.NewGuid(), WorkId = foreignWorkId, AssetId = foreignAssetId, Title = "Fahrenheit 451" },
            ],
        });

        var restored = Assert.Single(playback.AudiobookHistory);
        Assert.Equal("Project Hail Mary", restored.Title);
        Assert.Equal("Chapter One (Override)", restored.ChapterTitle);
        Assert.Equal(0, restored.ChapterIndex);

        var outsideTimeline = Assert.Single(PlaybackSessionController.ScopeAudiobookHistory(
            [staleBookHistory with { PositionSeconds = 1200 }],
            book,
            [book]));
        Assert.Equal("Project Hail Mary", outsideTimeline.Title);
        Assert.Null(outsideTimeline.ChapterTitle);
        Assert.Null(outsideTimeline.ChapterIndex);

        playback.ApplyPlayerState(new PlayerStateDto
        {
            Experience = PlayerExperienceModes.Audiobook,
            AudiobookHistory =
            [
                staleBookHistory with { ChapterTitle = "TV episode title again" },
                staleBookHistory with { Id = Guid.NewGuid(), WorkId = foreignWorkId, AssetId = foreignAssetId, Title = "Foreign title" },
            ],
        });

        var refreshed = Assert.Single(playback.AudiobookHistory);
        Assert.Equal("Project Hail Mary", refreshed.Title);
        Assert.Equal("Chapter One (Override)", refreshed.ChapterTitle);
        Assert.Equal(0, refreshed.ChapterIndex);
    }

    [Fact]
    public async Task CrossAssetAudiobookHistoryAndBookmarkDoNotReuseCurrentAssetStreamOrManifest()
    {
        var bookId = Guid.NewGuid();
        var chapterAssets = Enumerable.Range(0, 32).Select(_ => Guid.NewGuid()).ToArray();
        var currentAssetId = chapterAssets[0];
        var targetAssetId = chapterAssets[17];
        var current = CreateAudiobookItem("Book", "https://stale.example/current") with
        {
            WorkId = bookId,
            AudiobookWorkId = bookId,
            AssetId = currentAssetId,
            Manifest = new PlaybackManifestDto { AssetId = currentAssetId, DirectStreamUrl = "https://stale.example/manifest" },
            Chapters = chapterAssets.Select((assetId, index) => new PlaybackChapterDto
            {
                Index = index,
                AssetId = assetId,
                Title = index == 17 ? "Target chapter" : $"Chapter {index + 1}",
                StartSeconds = 0,
                EndSeconds = 500,
            }).ToArray(),
        };

        static PlaybackSessionController CreateController(ListenQueueItem item, Action<PlaybackTransportCommand?> setCommand)
        {
            var playback = new PlaybackSessionController(null!, null!);
            playback.RestoreState(new ListenPlaybackSnapshot
            {
                Queue = [item],
                CurrentIndex = 0,
                Experience = PlayerExperienceModes.Audiobook,
            });
            playback.TransportCommandRequested += command =>
            {
                setCommand(command);
                return Task.CompletedTask;
            };
            return playback;
        }

        PlaybackTransportCommand? historyCommand = null;
        var historyPlayback = CreateController(current, command => historyCommand = command);
        await historyPlayback.PlayAudiobookHistoryAsync(new AudiobookListenHistoryItemDto
        {
            Id = Guid.NewGuid(),
            WorkId = bookId,
            AssetId = targetAssetId,
            Title = "Book",
            ChapterTitle = "Target chapter",
            PositionSeconds = 42,
        });

        Assert.Equal(targetAssetId, historyPlayback.CurrentItem?.AssetId);
        Assert.Equal($"/engine-stream/{targetAssetId:D}", historyCommand?.StreamUrl);
        Assert.DoesNotContain("stale.example", historyCommand?.StreamUrl ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(32, historyPlayback.CurrentItem?.Chapters.Count);
        Assert.Equal(targetAssetId, historyPlayback.CurrentItem?.Chapters[17].AssetId);

        PlaybackTransportCommand? bookmarkCommand = null;
        var bookmarkPlayback = CreateController(current, command => bookmarkCommand = command);
        await bookmarkPlayback.PlayAudiobookBookmarkAsync(new AudiobookBookmarkDto
        {
            Id = Guid.NewGuid(),
            ProfileId = Guid.NewGuid(),
            WorkId = bookId,
            AssetId = targetAssetId,
            ChapterTitle = "Target chapter",
            PositionSeconds = 84,
        });

        Assert.Equal(targetAssetId, bookmarkPlayback.CurrentItem?.AssetId);
        Assert.Equal($"/engine-stream/{targetAssetId:D}", bookmarkCommand?.StreamUrl);
        Assert.DoesNotContain("stale.example", bookmarkCommand?.StreamUrl ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(32, bookmarkPlayback.CurrentItem?.Chapters.Count);
        Assert.Equal(targetAssetId, bookmarkPlayback.CurrentItem?.Chapters[17].AssetId);

        var profileId = Guid.NewGuid();
        var previewPlayback = new PlaybackSessionController(null!, null!, preferences: new ActiveProfilePlaybackPreferences(profileId));
        PlaybackTransportCommand? previewCommand = null;
        previewPlayback.TransportCommandRequested += command =>
        {
            previewCommand = command;
            return Task.CompletedTask;
        };
        await previewPlayback.PlayAudiobookAsync(current);
        var sessionLeaseId = Assert.IsType<Guid>(previewPlayback.AudiobookBookSessionLeaseId);
        var previewContext = new AudiobookBookmarkActionContext(Guid.NewGuid(), profileId, bookId,
            sessionLeaseId, previewPlayback.AudiobookBookSessionGeneration, currentAssetId);
        var previewDraft = new CapturedAudiobookBookmarkDraft(1, profileId, bookId, sessionLeaseId,
            targetAssetId, 17, "Target chapter", 84, 500, DateTimeOffset.UtcNow);

        Assert.True(await previewPlayback.PreviewCapturedAudiobookDraftAsync(previewContext, previewDraft));
        Assert.Equal(targetAssetId, previewPlayback.CurrentItem?.AssetId);
        Assert.Equal(84, previewPlayback.CurrentItem?.InitialPositionSeconds);
        Assert.Equal($"/engine-stream/{targetAssetId:D}", previewCommand?.StreamUrl);
        Assert.Equal(32, previewPlayback.CurrentItem?.Chapters.Count);
        Assert.Equal(targetAssetId, previewPlayback.CurrentItem?.Chapters[17].AssetId);
    }

    [Fact]
    public void EpisodeTitleHydrationRequiresTheActiveVideoRequest()
    {
        var episode = CreateVideoItem("S1 E1", "stream://episode") with
        {
            WorkId = Guid.NewGuid(),
            EpisodeNumber = "1",
            SeasonNumber = "1",
        };
        var playback = new PlaybackSessionController(null!, null!);
        playback.RestoreState(new ListenPlaybackSnapshot
        {
            Queue = [episode],
            CurrentIndex = 0,
            Experience = PlayerExperienceModes.Video,
            IsVideoExpanded = true,
        });
        var requestVersion = playback.PlaybackRequestVersion;

        Assert.False(playback.TrySetCurrentVideoEpisodeTitle(episode.WorkId, requestVersion - 1, "I'm Used to It"));
        Assert.Equal("S1 E1", playback.CurrentItem?.Title);
        Assert.True(playback.TrySetCurrentVideoEpisodeTitle(episode.WorkId, requestVersion, "I'm Used to It"));
        Assert.Equal("I'm Used to It", playback.CurrentItem?.Title);
    }

    [Fact]
    public void AudiobookContributorAndParentWorkIdsSurviveSnapshotsWithoutTitleInference()
    {
        var chapterId = Guid.NewGuid();
        var audiobookId = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        var narratorId = Guid.NewGuid();
        var item = CreateAudiobookItem("Embedded chapter", "stream://chapter") with
        {
            WorkId = chapterId,
            AudiobookWorkId = audiobookId,
            Authors = [new PlaybackContributorIdentity("Author One", authorId), new PlaybackContributorIdentity("Author Two")],
            Narrators = [new PlaybackContributorIdentity("Narrator", narratorId)],
        };
        var original = new PlaybackSessionController(null!, null!);
        original.RestoreState(new ListenPlaybackSnapshot { Queue = [item], CurrentIndex = 0 });
        var restored = new PlaybackSessionController(null!, null!);
        restored.RestoreState(original.CreateSnapshot());

        Assert.Equal($"/details/audiobook/{audiobookId:D}?context=listen", ListenPlaybackIdentityRoutes.Audiobook(restored.CurrentItem));
        Assert.Equal($"/details/person/{authorId:D}", ListenPlaybackIdentityRoutes.Contributor(restored.CurrentItem!.Authors[0]));
        Assert.Null(ListenPlaybackIdentityRoutes.Contributor(restored.CurrentItem.Authors[1]));
        Assert.Equal($"/details/person/{narratorId:D}", ListenPlaybackIdentityRoutes.Contributor(restored.CurrentItem.Narrators[0]));
        Assert.Null(ListenPlaybackIdentityRoutes.Audiobook(CreateAudiobookItem("audiobook by title", "stream://no-id") with { WorkId = Guid.Empty }));

        var legacy = CreateAudiobookItem("Legacy chapter", "stream://legacy") with { WorkId = chapterId, AlbumWorkId = audiobookId };
        Assert.Equal($"/details/audiobook/{audiobookId:D}?context=listen", ListenPlaybackIdentityRoutes.Audiobook(legacy));
    }

    private sealed class DelayedManifestHandler : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<HttpResponseMessage> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/manifest"))
            {
                Entered.TrySetResult();
                // Deliberately ignore cancellation: the controller must reject a stale response itself.
                return Response.Task;
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new PlayerStateDto()) });
        }
    }

    private static ListenQueueItem CreateQueueItem(string title, string streamUrl) => new()
    {
        WorkId = Guid.NewGuid(),
        MediaType = "Music",
        Title = title,
        Subtitle = "Artist",
        Album = "Album",
        Duration = "3:30",
        StreamUrl = streamUrl,
    };

    private static ListenQueueItem CreateAudiobookItem(string title, string streamUrl) => new()
    {
        WorkId = Guid.NewGuid(),
        AssetId = Guid.NewGuid(),
        MediaType = "Audiobooks",
        Title = title,
        Subtitle = "Matt Dinniman",
        Album = title,
        Duration = "11:32:00",
        StreamUrl = streamUrl,
    };

    private static ListenQueueItem CreateVideoItem(string title, string streamUrl) => new()
    {
        WorkId = Guid.NewGuid(),
        AssetId = Guid.NewGuid(),
        MediaType = "Movie",
        Title = title,
        Year = "2010",
        Duration = "2:28:00",
        StreamUrl = streamUrl,
        Manifest = new PlaybackManifestDto
        {
            MediaType = "Movie",
            DirectPlaySupported = true,
            DirectStreamUrl = streamUrl,
            Technical = new PlaybackTechnicalInfoDto
            {
                Width = 1920,
                Height = 1080,
                VideoCodec = "h264",
            },
        },
    };

    private sealed class PlayerSyncHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            object payload = request.RequestUri?.AbsolutePath.Contains("/history", StringComparison.OrdinalIgnoreCase) == true
                ? Array.Empty<AudiobookListenHistoryItemDto>()
                : new PlayerStateDto { Experience = PlayerExperienceModes.Audiobook, PlaybackRate = 1.25d };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(payload),
            });
        }
    }

    private sealed class TelemetryHeartbeatHandler(Guid profileId, Guid sessionId) : HttpMessageHandler
    {
        public List<PlayerHeartbeatDto> Heartbeats { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/profiles")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new[]
                    {
                        new
                        {
                            id = profileId,
                            display_name = "Viewer",
                            avatar_color = "#000000",
                            role = "RestrictedProfile",
                            created_at = DateTimeOffset.UtcNow,
                        },
                    }),
                };
            }
            if (request.RequestUri?.AbsolutePath == "/api/v1/player/heartbeat")
            {
                Heartbeats.Add((await request.Content!.ReadFromJsonAsync<PlayerHeartbeatDto>(cancellationToken))!);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new PlayerStateDto
                    {
                        SessionId = sessionId,
                        ProfileId = profileId,
                        Experience = PlayerExperienceModes.Audiobook,
                        PlaybackRate = 1,
                    }),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private sealed class NullJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult(default(TValue)!);
    }

    private sealed class BlockingManifestHandler : HttpMessageHandler
    {
        private readonly Guid _assetId;
        private readonly TaskCompletionSource<PlaybackManifestDto> _manifest = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BlockingManifestHandler(Guid assetId)
        {
            _assetId = assetId;
        }

        public void ReleaseManifest() =>
            _manifest.TrySetResult(new PlaybackManifestDto
            {
                AssetId = _assetId,
                MediaType = "Audiobooks",
                DirectPlaySupported = true,
                DirectStreamUrl = $"/stream/{_assetId:D}",
                Chapters =
                [
                    new PlaybackChapterDto
                    {
                        Index = 0,
                        Title = "Chapter 1",
                        StartSeconds = 0,
                        EndSeconds = 600,
                    },
                ],
            });

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.Contains("/manifest", StringComparison.OrdinalIgnoreCase) == true)
            {
                var manifest = await _manifest.Task.WaitAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(manifest),
                };
            }

            object payload = request.RequestUri?.AbsolutePath.Contains("/history", StringComparison.OrdinalIgnoreCase) == true
                ? Array.Empty<AudiobookListenHistoryItemDto>()
                : new PlayerStateDto { Experience = PlayerExperienceModes.Audiobook, PlaybackRate = 1.25d };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(payload),
            };
        }
    }

    private sealed class PlaybackPreferencesStub(UserPlaybackSettingsDto settings) : IUserPlaybackPreferencesAccessor
    {
        public Task<UserPlaybackSettingsDto?> GetAsync(CancellationToken ct = default) =>
            Task.FromResult<UserPlaybackSettingsDto?>(settings);

        public void UpdateCache(UserPlaybackSettingsDto next) { }

        public void Invalidate() { }
    }

    private sealed class ActiveProfilePlaybackPreferences(Guid profileId) : IUserPlaybackPreferencesAccessor
    {
        public Guid? ActiveProfileId => profileId;
        public Task<UserPlaybackSettingsDto?> GetAsync(CancellationToken ct = default) => Task.FromResult<UserPlaybackSettingsDto?>(null);
        public void UpdateCache(UserPlaybackSettingsDto settings) { }
        public void Invalidate() { }
    }

    private sealed class MutableActiveProfilePlaybackPreferences(Guid profileId) : IUserPlaybackPreferencesAccessor
    {
        public Guid? ActiveProfileId { get; set; } = profileId;
        public Task<UserPlaybackSettingsDto?> GetAsync(CancellationToken ct = default) => Task.FromResult<UserPlaybackSettingsDto?>(null);
        public void UpdateCache(UserPlaybackSettingsDto settings) { }
        public void Invalidate() { }
    }

    private sealed class PlaybackAssetResolutionHandler(Guid workId, Guid assetId) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri?.AbsolutePath.EndsWith($"/read/resolve/{workId:D}", StringComparison.OrdinalIgnoreCase) == true)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new { assetId }),
                    });
            }
            if (request.RequestUri?.AbsolutePath.EndsWith($"/playback/{assetId:D}/manifest", StringComparison.OrdinalIgnoreCase) == true)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new PlaybackManifestDto
                        {
                            AssetId = assetId,
                            MediaType = "Music",
                            DirectPlaySupported = true,
                            DirectStreamUrl = $"/media/assets/{assetId:D}/stream",
                        }),
                    });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class DelayedFirstPlaybackPreferences(UserPlaybackSettingsDto delayedSettings) : IUserPlaybackPreferencesAccessor
    {
        private int _calls;
        private readonly TaskCompletionSource<UserPlaybackSettingsDto?> _firstResult =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstCallEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<UserPlaybackSettingsDto?> GetAsync(CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                FirstCallEntered.TrySetResult();
                // This deliberately ignores cancellation to exercise the controller's version guard.
                return await _firstResult.Task;
            }

            return UserPlaybackSettingsDto.CreateDefaults(Guid.Empty);
        }

        public void ReleaseFirstCall() => _firstResult.TrySetResult(delayedSettings);
        public void UpdateCache(UserPlaybackSettingsDto next) { }
        public void Invalidate() { }
    }

    private sealed class DelayNextPlaybackSettings : IUserPlaybackPreferencesAccessor
    {
        private int _blockNextCall;
        private TaskCompletionSource<UserPlaybackSettingsDto?>? _blockedResult;
        public TaskCompletionSource BlockedCallEntered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void BlockNextCall()
        {
            _blockedResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
            BlockedCallEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _blockNextCall, 1);
        }

        public async Task<UserPlaybackSettingsDto?> GetAsync(CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref _blockNextCall, 0) == 1)
            {
                BlockedCallEntered.TrySetResult();
                return await _blockedResult!.Task;
            }

            return UserPlaybackSettingsDto.CreateDefaults(Guid.Empty);
        }

        public void ReleaseBlockedCall() => _blockedResult!.TrySetResult(UserPlaybackSettingsDto.CreateDefaults(Guid.Empty));
        public void UpdateCache(UserPlaybackSettingsDto next) { }
        public void Invalidate() { }
    }
}
