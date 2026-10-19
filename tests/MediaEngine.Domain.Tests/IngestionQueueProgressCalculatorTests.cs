using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Ingestion;

namespace MediaEngine.Domain.Tests;

public sealed class IngestionQueueProgressCalculatorTests
{
    private static readonly DateTimeOffset Base = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FiveHundredMixedFilesThroughEveryStep_NeverDecreaseAndNeverJumpByMoreThanTheStepsDone()
    {
        var sim = QueueSim.Mixed(500, Base);
        IngestionQueueProgressState? state = null;
        IngestionQueueProgressSnapshot? previous = null;
        var snapshots = new List<IngestionQueueProgressSnapshot>();

        for (var tick = 0; tick < 2000; tick++)
        {
            if (tick == 30)
            {
                sim.AddMixed(120, Base.AddSeconds(tick));
            }

            var now = Base.AddSeconds(tick);
            var result = IngestionQueueProgressCalculator.Calculate(sim.Rows(), state, now);
            state = result.State;
            var snapshot = result.Snapshot;

            if (previous is not null && previous.State != IngestionQueueRunState.Idle)
            {
                Assert.True(snapshot.Progress >= previous.Progress, $"Progress went backwards at tick {tick}.");
                var stepsDone = snapshot.StepsCompleted - previous.StepsCompleted;
                var allowed = (double)Math.Max(0, stepsDone) / snapshot.StepsTotal + 1e-9;
                Assert.True(
                    snapshot.Progress - previous.Progress <= allowed,
                    $"Progress jumped by {snapshot.Progress - previous.Progress:F4} at tick {tick}; only {allowed:F4} was done.");
            }

            snapshots.Add(snapshot);
            previous = snapshot;
            if (snapshot.State == IngestionQueueRunState.Idle)
            {
                break;
            }

            sim.Step(tick);
        }

        var last = snapshots[^1];
        Assert.Equal(IngestionQueueRunState.Idle, last.State);
        Assert.Equal(1d, last.Progress);
        Assert.Equal(620, last.FilesFound);
        Assert.Equal(620, last.FilesDone);
        Assert.Equal(120, last.FilesMoreFound);
        Assert.Equal(620L * 9, last.StepsTotal);
        Assert.Equal(last.StepsTotal, last.StepsCompleted);
        Assert.All(snapshots.Take(snapshots.Count - 1), snapshot => Assert.True(snapshot.Progress < 1d));
        Assert.DoesNotContain(snapshots, snapshot => snapshot.FilesMoreFound < 0);
    }

    [Fact]
    public void WhenIntakeFinishes_TheBarStaysTheSumOfEveryFilesStepOverTheStepsPossible()
    {
        var sim = QueueSim.Mixed(500, Base);
        IngestionQueueProgressState? state = null;
        var checkedAfterIntake = 0;

        for (var tick = 0; tick < 2000; tick++)
        {
            var result = IngestionQueueProgressCalculator.Calculate(sim.Rows(), state, Base.AddSeconds(tick));
            state = result.State;
            var snapshot = result.Snapshot;
            if (snapshot.State == IngestionQueueRunState.Idle)
            {
                break;
            }

            // With no files joining, the bar is exactly steps over steps possible before, at and after intake ends.
            Assert.Equal((double)snapshot.StepsCompleted / snapshot.StepsTotal, snapshot.Progress, 9);
            if (sim.IntakeFinished)
            {
                checkedAfterIntake++;
            }

            sim.Step(tick);
        }

        Assert.True(checkedAfterIntake > 10);
        Assert.True(sim.AllFinished);
    }

    [Fact]
    public void HeldFilesWithEverythingElseDone_ReportWaitingNotIdleAndNoEstimate()
    {
        var sim = QueueSim.Mixed(10, Base);
        sim.FinishAll();
        sim.Hold(3, "ffmpeg");

        var result = IngestionQueueProgressCalculator.Calculate(sim.Rows(), null, Base.AddMinutes(5));
        var snapshot = result.Snapshot;

        Assert.Equal(IngestionQueueRunState.Waiting, snapshot.State);
        Assert.Equal(3, snapshot.FilesWaiting);
        Assert.Equal(["ffmpeg"], snapshot.WaitingFor);
        Assert.Equal(10, snapshot.FilesFound);
        Assert.Equal(7, snapshot.FilesDone);
        Assert.True(snapshot.Progress < 1d);
        Assert.Null(snapshot.EstimatedSecondsRemaining);
    }

    [Fact]
    public void HeldFilesPlusOneFileStillWorking_ReportWorking()
    {
        var sim = QueueSim.Mixed(10, Base);
        sim.FinishAll();
        sim.Hold(3, "ffmpeg");
        sim.AddMixed(1, Base.AddMinutes(1));

        var snapshot = IngestionQueueProgressCalculator.Calculate(sim.Rows(), null, Base.AddMinutes(5)).Snapshot;

        Assert.Equal(IngestionQueueRunState.Working, snapshot.State);
        Assert.Equal(3, snapshot.FilesWaiting);
    }

    [Fact]
    public void HeldFilesAcrossTwoTools_ListEachToolOnce()
    {
        var sim = QueueSim.Mixed(6, Base);
        sim.FinishAll();
        sim.Hold(2, "ffmpeg");
        sim.Hold(2, "ffprobe", skip: 2);

        var snapshot = IngestionQueueProgressCalculator.Calculate(sim.Rows(), null, Base.AddMinutes(5)).Snapshot;

        Assert.Equal(["ffmpeg", "ffprobe"], snapshot.WaitingFor);
        Assert.Equal(4, snapshot.FilesWaiting);
    }

    [Fact]
    public void Tally_ShowsTypesAsTheyBecomeKnown_AndCountsUnknownAsUnsorted()
    {
        var sim = QueueSim.Mixed(60, Base);
        sim.SetPhaseForAll(2);
        var early = IngestionQueueProgressCalculator.Calculate(sim.Rows(), null, Base).Snapshot;

        Assert.Empty(early.Tally);
        Assert.Equal(60, early.FilesUnsorted);

        sim.SetPhaseForAll(3);
        var later = IngestionQueueProgressCalculator.Calculate(sim.Rows(), null, Base.AddSeconds(1)).Snapshot;

        Assert.Equal(
            ["Movies", "TV", "Music", "Books", "Audiobooks", "Comics"],
            later.Tally.Select(entry => entry.MediaType));
        Assert.Equal(60 - later.FilesUnsorted, later.Tally.Sum(entry => entry.Files));
        Assert.True(later.FilesUnsorted > 0);
        Assert.Equal(60, later.FilesFound);
    }

    [Fact]
    public void AlbumTracks_AreCountedAsIndividualFiles()
    {
        var sim = QueueSim.Empty(Base);
        sim.Add(12, "Music", QueueSim.Variant.Normal, Base);
        sim.SetPhaseForAll(6);

        var snapshot = IngestionQueueProgressCalculator.Calculate(sim.Rows(), null, Base.AddSeconds(1)).Snapshot;

        var music = Assert.Single(snapshot.Tally);
        Assert.Equal("Music", music.MediaType);
        Assert.Equal(12, music.Files);
        Assert.Equal(12, snapshot.FilesFound);
    }

    [Fact]
    public void TallyOrder_PutsOtherTypesAfterTheSixKnownOnes()
    {
        var sim = QueueSim.Empty(Base);
        sim.Add(1, "Photos", QueueSim.Variant.Normal, Base);
        sim.Add(1, "Comics", QueueSim.Variant.Normal, Base);
        sim.Add(1, "Movies", QueueSim.Variant.Normal, Base);
        sim.SetPhaseForAll(6);

        var snapshot = IngestionQueueProgressCalculator.Calculate(sim.Rows(), null, Base.AddSeconds(1)).Snapshot;

        Assert.Equal(["Movies", "Comics", "Photos"], snapshot.Tally.Select(entry => entry.MediaType));
    }

    [Fact]
    public void FinishedFiles_AreCountedByHowTheyEnded_AndTerminalFailuresCountAsDone()
    {
        var sim = QueueSim.Empty(Base);
        sim.Add(5, "Movies", QueueSim.Variant.Normal, Base);
        sim.Add(2, "Movies", QueueSim.Variant.FailEarly, Base);
        sim.Add(3, "TV", QueueSim.Variant.NoMatch, Base);
        sim.Add(1, "Books", QueueSim.Variant.IdentityFailed, Base);
        sim.Add(2, "Books", QueueSim.Variant.Review, Base);
        sim.FinishAll();

        var snapshot = IngestionQueueProgressCalculator.Calculate(sim.Rows(), null, Base.AddSeconds(1)).Snapshot;

        // Everything finished before the first look, so no period ever opened: nothing to report.
        Assert.Equal(IngestionQueueRunState.Idle, snapshot.State);

        sim.AddOpen(Base);
        var withOne = IngestionQueueProgressCalculator.Calculate(sim.Rows(), null, Base.AddSeconds(2)).Snapshot;
        Assert.Equal(14, withOne.FilesFound);
        Assert.Equal(13, withOne.FilesDone);
        Assert.Equal(new IngestionQueueOutcomes(Added: 5, NeedsReview: 3 + 2, Failed: 2 + 1, Skipped: 0), withOne.Outcomes);
        Assert.Equal(IngestionQueueRunState.Working, withOne.State);
    }

    [Fact]
    public void AFileThatAppearsAfterTheQueueWentIdle_StartsANewPeriodThatResetsTheBarAndTheStartTime()
    {
        var sim = QueueSim.Mixed(20, Base);
        IngestionQueueProgressState? state = null;
        IngestionQueueProgressSnapshot snapshot;
        var tick = 0;
        do
        {
            var result = IngestionQueueProgressCalculator.Calculate(sim.Rows(), state, Base.AddSeconds(tick));
            state = result.State;
            snapshot = result.Snapshot;
            sim.Step(tick++);
        }
        while (snapshot.State != IngestionQueueRunState.Idle && tick < 500);

        Assert.Equal(1d, snapshot.Progress);
        Assert.Equal(Base, snapshot.StartedAt);

        var later = Base.AddMinutes(30);
        sim.AddMixed(2, later);
        var next = IngestionQueueProgressCalculator.Calculate(sim.Rows(), state, later);

        Assert.True(next.StartedNewPeriod);
        Assert.Equal(IngestionQueueRunState.Working, next.Snapshot.State);
        Assert.Equal(later, next.Snapshot.StartedAt);
        Assert.Equal(2, next.Snapshot.FilesFound);
        Assert.Equal(0, next.Snapshot.FilesMoreFound);
        Assert.True(next.Snapshot.Progress < 0.5);
    }

    [Fact]
    public void NothingToDoAtStart_ReportsIdleWithNoPeriod()
    {
        var result = IngestionQueueProgressCalculator.Calculate([], null, Base);

        Assert.Equal(IngestionQueueRunState.Idle, result.Snapshot.State);
        Assert.Null(result.Snapshot.StartedAt);
        Assert.Equal(0, result.Snapshot.FilesFound);
        Assert.False(result.StartedNewPeriod);
    }

    [Fact]
    public void AnUnfinishedFileOlderThanTheEngineStart_StartsThePeriodAtItsOwnCreationTime()
    {
        var sim = QueueSim.Empty(Base);
        sim.Add(1, "Movies", QueueSim.Variant.Normal, Base.AddHours(-3));
        sim.SetPhaseForAll(9);

        var result = IngestionQueueProgressCalculator.Calculate(sim.Rows(), null, Base);

        Assert.True(result.StartedNewPeriod);
        Assert.Equal(Base.AddHours(-3), result.Snapshot.StartedAt);
        Assert.Equal(1, result.Snapshot.FilesFound);
    }

    [Fact]
    public void AFileThatRetriesAndShowsAnEarlierStage_NeverMovesBackwards()
    {
        var sim = QueueSim.Empty(Base);
        sim.Add(1, "Movies", QueueSim.Variant.Normal, Base);
        sim.SetPhaseForAll(4);
        var first = IngestionQueueProgressCalculator.Calculate(sim.Rows(), null, Base);

        sim.SetPhaseForAll(1);
        var second = IngestionQueueProgressCalculator.Calculate(sim.Rows(), first.State, Base.AddSeconds(1));

        Assert.True(second.Snapshot.StepsCompleted >= first.Snapshot.StepsCompleted);
    }

    [Fact]
    public void ReplayOfAFiveHundredFileRun_EstimateIsNullUntilFifteenPercentIsDone_AndStaysWithinTwentyFivePercent()
    {
        var run = ReplayRun();

        // Not vacuous: the replay does produce estimates, and some of them come before the run is nearly over.
        var estimates = run.Where(sample => sample.Snapshot.EstimatedSecondsRemaining is not null).ToList();
        Assert.True(estimates.Count >= 20, $"Only {estimates.Count} estimates were shown.");

        foreach (var (snapshot, actualRemaining) in run)
        {
            if (snapshot.EstimatedSecondsRemaining is not { } estimate)
            {
                continue;
            }

            var fraction = (double)snapshot.StepsCompleted / snapshot.StepsTotal;
            Assert.True(fraction >= 0.15, $"An estimate was shown at {fraction:P0} done.");
            Assert.True(
                Math.Abs(estimate - actualRemaining) <= 0.25 * actualRemaining + 1,
                $"At {(snapshot.UpdatedAt - Base).TotalSeconds:F0}s the estimate was {estimate}s but {actualRemaining:F0}s remained.");
        }
    }

    [Fact]
    public void SteadyRun_ShowsNoEstimateUntilFifteenPercentIsDone_ThenShowsOne()
    {
        var sim = QueueSim.Mixed(2000, Base, withVariants: false);
        IngestionQueueProgressState? state = null;
        var firstEstimateFraction = (double?)null;
        var looksBeforeIt = 0;

        for (var tick = 0; tick < 5000 && firstEstimateFraction is null; tick++)
        {
            var result = IngestionQueueProgressCalculator.Calculate(sim.Rows(), state, Base.AddSeconds(tick));
            state = result.State;
            if (result.Snapshot.EstimatedSecondsRemaining is not null)
            {
                firstEstimateFraction = (double)result.Snapshot.StepsCompleted / result.Snapshot.StepsTotal;
            }
            else
            {
                looksBeforeIt++;
            }

            sim.Step(tick, intakePerTick: 5, identityPerTick: 5);
        }

        Assert.NotNull(firstEstimateFraction);
        Assert.True(firstEstimateFraction >= 0.15, $"The first estimate came at {firstEstimateFraction:P0} done.");
        Assert.True(looksBeforeIt > 30, "The estimate should have waited for the work to be well under way.");
    }

    [Fact]
    public void ReplayOfAFiveHundredFileRun_ShowsNoEstimateWhileIntakeIsStillBursting()
    {
        var run = ReplayRun();

        var burst = run.Where(sample => (sample.Snapshot.UpdatedAt - Base).TotalSeconds < 30).ToList();

        Assert.NotEmpty(burst);
        Assert.All(burst, sample => Assert.Null(sample.Snapshot.EstimatedSecondsRemaining));
    }

    [Fact]
    public void EstimateIsNullWhileEveryUnfinishedFileIsWaiting()
    {
        var sim = QueueSim.Mixed(200, Base);
        IngestionQueueProgressState? state = null;
        IngestionQueueProgressSnapshot snapshot = IngestionQueueProgressSnapshot.Empty(Base);
        for (var tick = 0; tick < 400 && !sim.AllFinished; tick++)
        {
            var result = IngestionQueueProgressCalculator.Calculate(sim.Rows(), state, Base.AddSeconds(tick));
            snapshot = result.Snapshot;
            state = result.State;
            sim.Step(tick);
        }

        sim.FinishAll();
        sim.Hold(sim.Count, "ffmpeg");
        var waiting = IngestionQueueProgressCalculator.Calculate(sim.Rows(), state, Base.AddSeconds(500)).Snapshot;

        Assert.Equal(IngestionQueueRunState.Waiting, waiting.State);
        Assert.Null(waiting.EstimatedSecondsRemaining);
    }

    /// <summary>
    /// A recorded-run stand-in: 500 files noticed together and read at once (fast intake), identity work then proceeding
    /// at two files a second per stage, with a 30 second stall in the middle. Returns each look at the queue with how
    /// many seconds really remained.
    /// </summary>
    private static List<(IngestionQueueProgressSnapshot Snapshot, double ActualRemainingSeconds)> ReplayRun()
    {
        var sim = QueueSim.Mixed(500, Base, withVariants: false);
        IngestionQueueProgressState? state = null;
        var looks = new List<IngestionQueueProgressSnapshot>();
        var finishedAt = 0;
        for (var tick = 0; tick < 3000; tick++)
        {
            var result = IngestionQueueProgressCalculator.Calculate(sim.Rows(), state, Base.AddSeconds(tick));
            state = result.State;
            looks.Add(result.Snapshot);
            if (result.Snapshot.State == IngestionQueueRunState.Idle)
            {
                finishedAt = tick;
                break;
            }

            var stalled = tick is >= 100 and < 130;
            sim.Step(tick, intakePerTick: stalled ? 0 : 500, identityPerTick: stalled ? 0 : 2);
        }

        Assert.True(finishedAt > 0, "The replayed run never finished.");
        return looks
            .Select((look, tick) => (look, (double)(finishedAt - tick)))
            .Where(pair => pair.Item2 > 0)
            .ToList();
    }

    /// <summary>A deterministic stand-in for the pipeline: files move through the phases the data store shows.</summary>
    private sealed class QueueSim
    {
        public enum Variant { Normal, FailEarly, NoMatch, IdentityFailed, Review }

        private const int Finished = 11;
        private const int FailedEarly = 98;
        private const int NoMatched = 97;
        private const int IdentityFailedPhase = 96;

        private static readonly string[] Types = ["Movies", "TV", "Music", "Books", "Audiobooks", "Comics", "Unknown"];

        private readonly List<SimFile> _files = [];
        private int _created;

        private QueueSim()
        {
        }

        public int Count => _files.Count;

        public bool IntakeFinished => _files.All(file => file.Phase >= 5);

        public bool AllFinished => _files.All(file => file.Phase >= Finished && file.HoldTool is null);

        public static QueueSim Empty(DateTimeOffset createdAt) => new();

        public static QueueSim Mixed(int count, DateTimeOffset createdAt, bool withVariants = true)
        {
            var sim = new QueueSim();
            sim.AddMixed(count, createdAt, withVariants);
            return sim;
        }

        public void AddMixed(int count, DateTimeOffset createdAt, bool withVariants = true)
        {
            for (var i = 0; i < count; i++)
            {
                var n = _created;
                var variant = !withVariants ? Variant.Normal
                    : n % 25 == 24 ? Variant.FailEarly
                    : n % 31 == 30 ? Variant.NoMatch
                    : n % 47 == 46 ? Variant.IdentityFailed
                    : n % 53 == 52 ? Variant.Review
                    : Variant.Normal;
                Add(1, Types[n % Types.Length], variant, createdAt);
            }
        }

        public void Add(int count, string type, Variant variant, DateTimeOffset createdAt)
        {
            for (var i = 0; i < count; i++)
            {
                _created++;
                _files.Add(new SimFile(Guid.NewGuid(), Guid.NewGuid(), createdAt, type, variant));
            }
        }

        /// <summary>Adds one file that is still being worked on.</summary>
        public void AddOpen(DateTimeOffset createdAt) => Add(1, "Movies", Variant.Normal, createdAt);

        public void SetPhaseForAll(int phase)
        {
            foreach (var file in _files)
            {
                file.Phase = phase;
            }
        }

        public void FinishAll()
        {
            foreach (var file in _files)
            {
                file.Phase = file.Variant switch
                {
                    Variant.FailEarly => FailedEarly,
                    Variant.NoMatch => NoMatched,
                    Variant.IdentityFailed => IdentityFailedPhase,
                    _ => Finished,
                };
            }
        }

        public void Hold(int count, string toolId, int skip = 0)
        {
            foreach (var file in _files.Where(file => file.Phase == Finished).Skip(skip).Take(count))
            {
                file.HoldTool = toolId;
            }
        }

        /// <summary>One second of pipeline work: fast intake, then limited identity work per stage.</summary>
        public void Step(int tick, int intakePerTick = 500, int identityPerTick = 4)
        {
            var intake = 0;
            foreach (var file in _files.Where(file => file.Phase < 5).OrderBy(file => file.Created))
            {
                if (intake++ >= intakePerTick)
                {
                    break;
                }

                Advance(file);
            }

            foreach (var file in _files.Where(file => file.Phase == 5))
            {
                Advance(file);
            }

            for (var stage = 10; stage >= 6; stage--)
            {
                var moved = 0;
                foreach (var file in _files.Where(file => file.Phase == stage))
                {
                    if (moved++ >= identityPerTick)
                    {
                        break;
                    }

                    Advance(file);
                }
            }
        }

        private static void Advance(SimFile file)
        {
            file.Phase++;
            if (file.Variant == Variant.FailEarly && file.Phase == 4)
            {
                file.Phase = FailedEarly;
            }
            else if (file.Variant == Variant.NoMatch && file.Phase == 8)
            {
                file.Phase = NoMatched;
            }
            else if (file.Variant == Variant.IdentityFailed && file.Phase == 9)
            {
                file.Phase = IdentityFailedPhase;
            }
        }

        public IReadOnlyList<IngestionQueueFileRow> Rows() => _files.Select(ToRow).ToList();

        private static IngestionQueueFileRow ToRow(SimFile file)
        {
            var phase = file.Phase;
            var registered = phase >= 5 && phase != FailedEarly;
            var typeKnown = phase >= 3 && phase != FailedEarly;
            var asset = registered ? file.AssetId : (Guid?)null;
            var (stage, status) = phase switch
            {
                0 => (MediaOperationStage.Discovered, MediaOperationStatus.Pending),
                1 => (MediaOperationStage.Queued, MediaOperationStatus.Queued),
                2 => (MediaOperationStage.Hashing, MediaOperationStatus.Running),
                3 => (MediaOperationStage.Parsing, MediaOperationStatus.Running),
                4 => (MediaOperationStage.Scoring, MediaOperationStatus.Running),
                5 => (MediaOperationStage.Registered, MediaOperationStatus.Running),
                FailedEarly => (MediaOperationStage.Failed, MediaOperationStatus.FailedTerminal),
                >= 6 and <= 10 => (MediaOperationStage.QueuedIdentity, MediaOperationStatus.Running),
                _ => (MediaOperationStage.Completed, MediaOperationStatus.Succeeded),
            };
            var identity = phase switch
            {
                6 => "Queued",
                7 => "RetailSearching",
                8 => "RetailMatched",
                9 => "Hydrating",
                10 => "UniverseEnriching",
                Finished => "Ready",
                NoMatched => "RetailNoMatch",
                IdentityFailedPhase => "Failed",
                _ => null,
            };
            var type = phase >= 6 && file.Type == "Unknown" ? "Unknown" : typeKnown && file.Type != "Unknown" ? file.Type : null;

            return new IngestionQueueFileRow(
                file.OperationId,
                file.Created,
                file.Created,
                stage,
                status,
                null,
                asset,
                identity,
                identity is null ? null : file.Created,
                type,
                file.HoldTool,
                file.Variant == Variant.Review && phase == Finished);
        }

        private sealed class SimFile(Guid operationId, Guid assetId, DateTimeOffset created, string type, Variant variant)
        {
            public Guid OperationId { get; } = operationId;

            public Guid AssetId { get; } = assetId;

            public DateTimeOffset Created { get; } = created;

            public string Type { get; } = type;

            public Variant Variant { get; } = variant;

            public int Phase { get; set; }

            public string? HoldTool { get; set; }
        }
    }
}
