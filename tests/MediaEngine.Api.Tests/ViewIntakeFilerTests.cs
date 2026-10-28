using Dapper;
using MediaEngine.Api.Services.LocalAssets;
using MediaEngine.Api.Services.LocalAssets.Intake;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using MediaEngine.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

public sealed class ViewIntakeFilerTests
{
    [Fact]
    public async Task APhotoWithADateInItsName_IsFiledUnderThatDay_KeepingItsOwnName()
    {
        using var fixture = new IntakeFixture();
        fixture.Drop(ViewContentKinds.Photos, "IMG_20240315_101500.jpg", IntakeFixture.Jpeg(1));

        var pass = await fixture.Filer.FilePendingAsync();

        var result = Assert.Single(pass.Groups);
        Assert.Equal(IntakeOutcomes.Filed, result.Outcome);
        var expected = Path.Combine(fixture.LibraryFolder, "2024", "2024-03-15", "IMG_20240315_101500.jpg");
        Assert.True(File.Exists(expected), expected);
        Assert.False(File.Exists(Path.Combine(fixture.SectionPath(ViewContentKinds.Photos), "IMG_20240315_101500.jpg")));
        Assert.NotNull(fixture.Repository.Find(Assert.NotNull(result.ItemId)));
    }

    [Fact]
    public async Task APhotoWithNoDateAnywhere_IsFiledUnderTheDateOfTheFile()
    {
        using var fixture = new IntakeFixture();
        var path = fixture.Drop(ViewContentKinds.Photos, "holiday.jpg", IntakeFixture.Jpeg(2));
        File.SetLastWriteTimeUtc(path, new DateTime(2021, 7, 4, 12, 0, 0, DateTimeKind.Utc));
        File.SetCreationTimeUtc(path, new DateTime(2021, 7, 4, 12, 0, 0, DateTimeKind.Utc));

        await fixture.Filer.FilePendingAsync();

        Assert.True(File.Exists(Path.Combine(fixture.LibraryFolder, "2021", "2021-07-04", "holiday.jpg")));
    }

    [Fact]
    public async Task ANewFile_IsLeftAloneUntilItHasStayedTheSameForTheSettleTime()
    {
        using var fixture = new IntakeFixture(settleSeconds: 30);
        var path = fixture.Drop(ViewContentKinds.Photos, "IMG_20240315_101500.jpg", IntakeFixture.Jpeg(3));

        var first = await fixture.Filer.FilePendingAsync();
        Assert.Empty(first.Groups);
        Assert.True(File.Exists(path));

        fixture.Clock.Advance(TimeSpan.FromSeconds(31));
        var second = await fixture.Filer.FilePendingAsync();

        Assert.Equal(IntakeOutcomes.Filed, Assert.Single(second.Groups).Outcome);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task AFileThatKeepsGrowing_IsNotFiledHalfCopied()
    {
        using var fixture = new IntakeFixture(settleSeconds: 30);
        var path = fixture.Drop(ViewContentKinds.Photos, "IMG_20240315_101500.jpg", IntakeFixture.Jpeg(4));
        await fixture.Filer.FilePendingAsync();

        fixture.Clock.Advance(TimeSpan.FromSeconds(31));
        File.WriteAllBytes(path, IntakeFixture.Jpeg(4, extra: 200));
        var pass = await fixture.Filer.FilePendingAsync();

        Assert.Empty(pass.Groups);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task ATruncatedPhoto_IsRefusedIntoTheRejectedAreaWithAReasonAndAnExpiry()
    {
        using var fixture = new IntakeFixture();
        fixture.Drop(ViewContentKinds.Photos, "IMG_20240315_101500.jpg", [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5, 6]);

        var pass = await fixture.Filer.FilePendingAsync();

        var result = Assert.Single(pass.Groups);
        Assert.Equal(IntakeOutcomes.Rejected, result.Outcome);
        Assert.Equal(ViewIntakeReasons.Damaged, result.ReasonCode);
        Assert.Equal("damaged or unreadable", result.ReasonText);
        var rejected = Assert.Single(await fixture.Intake.GetRejectedAsync(null, 10));
        Assert.StartsWith(fixture.Areas.RejectedPath(ViewContentKinds.Photos)!, rejected.CurrentPath);
        Assert.True(File.Exists(rejected.CurrentPath));
        Assert.InRange((rejected.ExpiresAt!.Value - fixture.Clock.GetUtcNow()).TotalDays, 29.9, 30.1);
        Assert.False(Directory.Exists(fixture.LibraryFolder) && Directory.GetFiles(fixture.LibraryFolder, "*", SearchOption.AllDirectories).Length > 0);
    }

    [Fact]
    public async Task AnEmptyFile_IsRefusedAsEmpty()
    {
        using var fixture = new IntakeFixture();
        fixture.Drop(ViewContentKinds.Photos, "IMG_20240315_101500.jpg", []);

        var result = Assert.Single((await fixture.Filer.FilePendingAsync()).Groups);

        Assert.Equal(IntakeOutcomes.Rejected, result.Outcome);
        Assert.Equal(ViewIntakeReasons.Empty, result.ReasonCode);
    }

    [Fact]
    public async Task AFileLibraryCannotRead_IsRefusedAsNotAPhotoOrVideoType()
    {
        using var fixture = new IntakeFixture();
        fixture.Drop(ViewContentKinds.Photos, "notes.xyz", [1, 2, 3]);

        var result = Assert.Single((await fixture.Filer.FilePendingAsync()).Groups);

        Assert.Equal(IntakeOutcomes.Rejected, result.Outcome);
        Assert.Equal(ViewIntakeReasons.UnsupportedType, result.ReasonCode);
    }

    [Fact]
    public async Task AnExactCopy_IsNotFiledTwice_WhenTheRuleIsKeepOne()
    {
        using var fixture = new IntakeFixture();
        fixture.Drop(ViewContentKinds.Photos, "IMG_20240315_101500.jpg", IntakeFixture.Jpeg(5));
        await fixture.Filer.FilePendingAsync();
        var second = fixture.Drop(ViewContentKinds.Photos, "copy of it.jpg", IntakeFixture.Jpeg(5));

        var pass = await fixture.Filer.FilePendingAsync();

        var result = Assert.Single(pass.Groups);
        Assert.Equal(IntakeOutcomes.Duplicate, result.Outcome);
        Assert.False(File.Exists(second));
        Assert.Single(Directory.GetFiles(fixture.LibraryFolder, "*", SearchOption.AllDirectories));
        Assert.Equal(1, pass.Duplicates);
    }

    [Fact]
    public async Task AnExactCopy_IsKeptAlongsideTheOriginal_WhenTheRuleIsKeepBoth()
    {
        using var fixture = new IntakeFixture(duplicatePolicy: LibraryDuplicatePolicies.KeepBoth);
        fixture.Drop(ViewContentKinds.Photos, "IMG_20240315_101500.jpg", IntakeFixture.Jpeg(6));
        await fixture.Filer.FilePendingAsync();
        fixture.Drop(ViewContentKinds.Photos, "IMG_20240315_101501.jpg", IntakeFixture.Jpeg(6));

        var result = Assert.Single((await fixture.Filer.FilePendingAsync()).Groups);

        Assert.Equal(IntakeOutcomes.Duplicate, result.Outcome);
        Assert.Equal(2, Directory.GetFiles(fixture.LibraryFolder, "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task TheDuplicatesJob_ReportsExactCopiesInOneSpace_AndKeepOneSendsTheRestToTrash()
    {
        using var fixture = new IntakeFixture();
        fixture.Drop(ViewContentKinds.Photos, "IMG_20240315_101500.jpg", IntakeFixture.Jpeg(12));
        await fixture.Filer.FilePendingAsync();
        fixture.CopyFirstItem();

        Assert.Equal(1, await fixture.Duplicates.ScanAsync());
        var report = await fixture.Duplicates.GetReportAsync(fixture.OwnerProfileId);
        var group = Assert.Single(report.Groups);
        Assert.Equal(2, group.Copies.Count);

        var outsider = await fixture.Duplicates.GetReportAsync(Guid.NewGuid());
        Assert.Empty(outsider.Groups);
        Assert.Null(await fixture.Duplicates.KeepOneAsync(Guid.NewGuid(), group.Id, group.Copies[0].ItemId));

        var result = await fixture.Duplicates.KeepOneAsync(fixture.OwnerProfileId, group.Id, group.Copies[0].ItemId);

        Assert.Equal(1, result!.MovedToTrash);
        Assert.Empty((await fixture.Duplicates.GetReportAsync(fixture.OwnerProfileId)).Groups);
        Assert.NotNull(fixture.Repository.Find(group.Copies[0].ItemId));
        Assert.NotNull(fixture.Repository.Find(group.Copies[1].ItemId)!.TrashedAt);
        Assert.Equal(0, await fixture.Duplicates.ScanAsync());
    }

    [Fact]
    public async Task TidyThisFolder_RenamesInPlaceIntoTheNewLayout_AndTheLibraryFindsTheFilesAtTheirNewPlace()
    {
        using var fixture = new IntakeFixture();
        fixture.Drop(ViewContentKinds.Photos, "IMG_20240315_101500.jpg", IntakeFixture.Jpeg(13));
        fixture.Drop(ViewContentKinds.Photos, "IMG_20230102_090000.jpg", IntakeFixture.Jpeg(14));
        await fixture.Filer.FilePendingAsync();
        var sourceId = await fixture.FolderSourceIdAsync();
        Assert.Equal(0, (await fixture.Tidy.GetStatusAsync(sourceId))!.WillMove);

        fixture.SetLayout("{year}/{month}");
        var preview = await fixture.Tidy.GetStatusAsync(sourceId);
        Assert.Equal(2, preview!.WillMove);

        Assert.Null(await fixture.Tidy.GetStatusAsync(sourceId, actor: Guid.NewGuid()));
        Assert.Null(await fixture.Tidy.StartAsync(sourceId, Guid.NewGuid(), "now", checkOwner: true));
        await fixture.Tidy.StartAsync(sourceId, fixture.OwnerProfileId, "now", checkOwner: true);
        Assert.True((await fixture.Tidy.GetStatusAsync(sourceId, actor: fixture.OwnerProfileId))!.Running);
        await fixture.Tidy.RunPendingAsync();

        var after = (await fixture.Tidy.GetStatusAsync(sourceId))!;
        Assert.False(after.Running);
        Assert.Equal(2, after.Moved);
        Assert.Equal(0, after.WillMove);
        Assert.True(File.Exists(Path.Combine(fixture.LibraryFolder, "2024", "03", "IMG_20240315_101500.jpg")));
        Assert.True(File.Exists(Path.Combine(fixture.LibraryFolder, "2023", "01", "IMG_20230102_090000.jpg")));
        Assert.False(Directory.Exists(Path.Combine(fixture.LibraryFolder, "2024", "2024-03-15")));
        var recorded = (await fixture.TidyRepository.GetSourceFilesAsync(sourceId)).Select(file => file.FilePath).ToList();
        Assert.All(recorded, path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public async Task ATidyRunThatWasStopped_ResumesFromItsLedgerWithoutLosingOrRepeatingAFile()
    {
        using var fixture = new IntakeFixture();
        fixture.Drop(ViewContentKinds.Photos, "IMG_20240315_101500.jpg", IntakeFixture.Jpeg(15));
        fixture.Drop(ViewContentKinds.Photos, "IMG_20230102_090000.jpg", IntakeFixture.Jpeg(16));
        await fixture.Filer.FilePendingAsync();
        var sourceId = await fixture.FolderSourceIdAsync();
        fixture.SetLayout("{year}/{month}");
        await fixture.Tidy.StartAsync(sourceId, null, "scheduled");

        // The Engine stopped after one file was renamed but before it was recorded.
        var first = (await fixture.TidyRepository.GetPlannedMovesAsync((await fixture.TidyRepository.GetRunningJobsAsync()).Single().Id)).First();
        Directory.CreateDirectory(Path.GetDirectoryName(first.ToPath)!);
        File.Move(first.FromPath, first.ToPath);

        await fixture.Tidy.RunPendingAsync();
        await fixture.Tidy.RunPendingAsync();

        var after = (await fixture.Tidy.GetStatusAsync(sourceId))!;
        Assert.Equal(2, after.Moved);
        Assert.Equal(0, after.Failed);
        Assert.Equal(2, Directory.GetFiles(fixture.LibraryFolder, "*.jpg", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task WhileAFolderIsBeingTidied_NewFilesWaitInsteadOfLandingInTheMiddleOfTheRun()
    {
        using var fixture = new IntakeFixture();
        fixture.Drop(ViewContentKinds.Photos, "IMG_20240315_101500.jpg", IntakeFixture.Jpeg(17));
        await fixture.Filer.FilePendingAsync();
        var sourceId = await fixture.FolderSourceIdAsync();
        fixture.SetLayout("{year}/{month}");
        await fixture.Tidy.StartAsync(sourceId, null, "now");
        var arriving = fixture.Drop(ViewContentKinds.Photos, "IMG_20240401_080000.jpg", IntakeFixture.Jpeg(18));

        var pass = await fixture.Filer.FilePendingAsync();

        Assert.Equal(IntakeOutcomes.Deferred, Assert.Single(pass.Groups).Outcome);
        Assert.True(File.Exists(arriving));
        await fixture.Tidy.RunPendingAsync();
        Assert.Equal(IntakeOutcomes.Filed, Assert.Single((await fixture.Filer.FilePendingAsync()).Groups).Outcome);
    }

    [Fact]
    public async Task AnExactCopyOfSomethingInTrash_IsRefusedWithAPlainSentence_AndNotDeleted()
    {
        using var fixture = new IntakeFixture();
        fixture.Drop(ViewContentKinds.Photos, "IMG_20240315_101500.jpg", IntakeFixture.Jpeg(7));
        var first = Assert.Single((await fixture.Filer.FilePendingAsync()).Groups);
        await fixture.Repository.SetLifecycleStateAsync(first.ItemId!.Value, LocalAssetLifecycleState.Trashed);
        fixture.Drop(ViewContentKinds.Photos, "again.jpg", IntakeFixture.Jpeg(7));

        var result = Assert.Single((await fixture.Filer.FilePendingAsync()).Groups);

        Assert.Equal(IntakeOutcomes.Rejected, result.Outcome);
        Assert.Equal("already in your library (in Trash)", result.ReasonText);
        Assert.True(result.DuplicateInTrash);
        Assert.True(File.Exists(Assert.Single(await fixture.Intake.GetRejectedAsync(null, 10)).CurrentPath));
    }

    [Fact]
    public async Task TwoDifferentPhotosWithTheSameNameOnTheSameDay_GetAShortNumber()
    {
        using var fixture = new IntakeFixture();
        fixture.Drop(ViewContentKinds.Photos, "IMG_20240315_101500.jpg", IntakeFixture.Jpeg(8));
        await fixture.Filer.FilePendingAsync();
        fixture.Drop(ViewContentKinds.Photos, "IMG_20240315_101500.jpg", IntakeFixture.Jpeg(9));

        await fixture.Filer.FilePendingAsync();

        var folder = Path.Combine(fixture.LibraryFolder, "2024", "2024-03-15");
        Assert.True(File.Exists(Path.Combine(folder, "IMG_20240315_101500.jpg")));
        Assert.True(File.Exists(Path.Combine(folder, "IMG_20240315_101500 (2).jpg")));
    }

    [Fact]
    public async Task ARawPhotoItsJpegAndItsSidecar_AreFiledTogetherAsOneItem()
    {
        using var fixture = new IntakeFixture();
        fixture.Drop(ViewContentKinds.Photos, "DSC_20240315_101500.NEF", [3, 3, 3, 3]);
        fixture.Drop(ViewContentKinds.Photos, "DSC_20240315_101500.JPG", IntakeFixture.Jpeg(10));
        fixture.Drop(ViewContentKinds.Photos, "DSC_20240315_101500.XMP", [5, 5, 5]);

        var result = Assert.Single((await fixture.Filer.FilePendingAsync()).Groups);

        Assert.Equal(IntakeOutcomes.Filed, result.Outcome);
        Assert.Equal(3, result.FileCount);
        var folder = Path.Combine(fixture.LibraryFolder, "2024", "2024-03-15");
        Assert.Equal(3, Directory.GetFiles(folder).Length);
        var item = Assert.IsType<MediaEngine.Contracts.LocalAssets.LocalAssetDto>(fixture.Repository.Find(result.ItemId!.Value));
        Assert.Equal(3, item.Files.Count);
    }

    [Fact]
    public async Task WhenThereIsNotEnoughRoom_TheFilesStayWhereTheyAreAndTheReasonIsRecorded()
    {
        using var fixture = new IntakeFixture();
        var path = fixture.Drop(ViewContentKinds.Photos, "IMG_20240315_101500.jpg", IntakeFixture.Jpeg(11));
        fixture.Filer.FreeBytesAt = _ => 1024;

        var result = Assert.Single((await fixture.Filer.FilePendingAsync()).Groups);

        Assert.Equal(IntakeOutcomes.Deferred, result.Outcome);
        Assert.Equal(ViewIntakeReasons.NotEnoughSpace, result.ReasonCode);
        Assert.StartsWith("Not enough space:", result.ReasonText);
        Assert.True(File.Exists(path));
        Assert.Equal(1, await fixture.Intake.CountWaitingAsync(null));

        fixture.Filer.FreeBytesAt = _ => long.MaxValue;
        Assert.Equal(IntakeOutcomes.Filed, Assert.Single((await fixture.Filer.FilePendingAsync()).Groups).Outcome);
    }

    [Fact]
    public async Task AGroupLeftHalfMovedByAStop_IsFinishedTheNextTimeLibraryStarts()
    {
        using var fixture = new IntakeFixture();
        var from = fixture.Drop(ViewContentKinds.Photos, "IMG_20240315_101500.jpg", IntakeFixture.Jpeg(12));
        var target = Path.Combine(fixture.LibraryFolder, "2024", "2024-03-15", "IMG_20240315_101500.jpg");
        var now = fixture.Clock.GetUtcNow();
        var row = await fixture.Intake.AddAsync(new ViewIntakeFile(
            Guid.NewGuid(), ViewContentKinds.Photos, IntakeDestination.Personal, fixture.OwnerProfileId, null,
            "IMG_20240315_101500.jpg", from, null, null, "k", LocalAssetFileRoles.Primary, new FileInfo(from).Length, null,
            ViewIntakeStates.Waiting, null, null, now, now, null));
        await fixture.Intake.UpdateAsync(row.Id, ViewIntakeStates.Filing, targetPath: target);

        await fixture.Filer.RecoverAsync();

        Assert.True(File.Exists(target));
        Assert.False(File.Exists(from));
        Assert.Equal(ViewIntakeStates.Filed, (await fixture.Intake.GetAsync(row.Id))!.State);
    }

    [Fact]
    public async Task ARefusedFileIsRemovedWhenItsTimeIsUp_AndAStrangerInTheRejectedAreaIsOnlyNoted()
    {
        using var fixture = new IntakeFixture();
        fixture.Drop(ViewContentKinds.Photos, "IMG_20240315_101500.jpg", []);
        await fixture.Filer.FilePendingAsync();
        var rejected = Assert.Single(await fixture.Intake.GetRejectedAsync(null, 10));
        var stranger = Path.Combine(fixture.Areas.RejectedPath(ViewContentKinds.Photos)!, "someone put this here.jpg");
        File.WriteAllBytes(stranger, [9, 9, 9]);

        fixture.Clock.Advance(TimeSpan.FromDays(31));
        await fixture.Filer.FilePendingAsync();

        Assert.False(File.Exists(rejected.CurrentPath));
        Assert.True(File.Exists(stranger));
        Assert.Contains(await fixture.Intake.GetByStateAsync([ViewIntakeStates.Stray], 10), row => row.CurrentPath == stranger);
    }

    [Fact]
    public async Task ANestedPersonFolder_SendsThePhotoToThatPersonsLibrary()
    {
        using var fixture = new IntakeFixture();
        var second = await fixture.AddProfileAsync("Mia");
        fixture.Drop(ViewContentKinds.Photos, Path.Combine("Mia", "IMG_20240315_101500.jpg"), IntakeFixture.Jpeg(13));

        var result = Assert.Single((await fixture.Filer.FilePendingAsync()).Groups);

        Assert.Equal(second, result.Destination.OwnerProfileId);
        var item = Assert.IsType<MediaEngine.Contracts.LocalAssets.LocalAssetDto>(fixture.Repository.Find(result.ItemId!.Value));
        Assert.Equal(second, item.OwnerProfileId);
    }

    [Fact]
    public async Task AnUpload_IsFiledByDayThroughTheSameFiler()
    {
        using var fixture = new IntakeFixture();
        await using var content = new MemoryStream(IntakeFixture.Jpeg(14));

        var result = await fixture.Filer.FileUploadAsync(fixture.OwnerProfileId, "IMG_20230102_030405.jpg", content, null);

        Assert.Equal(IntakeOutcomes.Filed, result.Outcome);
        Assert.True(File.Exists(Path.Combine(fixture.LibraryFolder, "2023", "2023-01-02", "IMG_20230102_030405.jpg")));
        Assert.Empty(Directory.GetFiles(fixture.Areas.UploadWorkingPath()!));
    }

    [Fact]
    public async Task AnUploadThatWillNotFit_IsRefusedWithTheSpaceMessage_AndLeavesNothingBehind()
    {
        using var fixture = new IntakeFixture();
        fixture.Filer.FreeBytesAt = _ => 10;
        await using var content = new MemoryStream(IntakeFixture.Jpeg(15));

        var refused = await Assert.ThrowsAsync<IntakeRefusedException>(() =>
            fixture.Filer.FileUploadAsync(fixture.OwnerProfileId, "IMG_20230102_030405.jpg", content, null));

        Assert.Equal(507, refused.StatusCode);
        Assert.StartsWith("Not enough space:", refused.Message);
        Assert.Equal(0, await fixture.Intake.CountWaitingAsync(null));
        Assert.Empty(Directory.GetFiles(fixture.SectionPath(ViewContentKinds.Photos), "*", SearchOption.AllDirectories));
    }

    private sealed class StepClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class IntakeFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima-intake-{Guid.NewGuid():N}");
        private readonly DatabaseConnection _database;
        private readonly ConfigurationDirectoryLoader _configuration;

        public IntakeFixture(int settleSeconds = 0, string duplicatePolicy = LibraryDuplicatePolicies.KeepOne)
        {
            var personal = Path.Combine(_root, "personal");
            Directory.CreateDirectory(personal);
            _database = new DatabaseConnection(Path.Combine(_root, "view.db"));
            _database.InitializeSchema();
            Repository = new LocalAssetRepository(_database);
            _configuration = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
            OwnerProfileId = Guid.NewGuid();
            ProfileTestData.InsertAsync(_database, new Profile
            {
                Id = OwnerProfileId,
                DisplayName = "Owner",
                Role = ProfileRole.Administrator,
                CreatedAt = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
            }).GetAwaiter().GetResult();
            _configuration.SaveLibraries(new LibrariesConfiguration
            {
                StorageLocations = [new ServerStorageLocationConfig { Id = "personal", Label = "Personal", Path = personal, AllowWrite = true }],
                ViewStorage = new ViewStorageConfig { StorageLocationId = "personal", RelativeRoot = "managed-view" },
                FileHandling = new FileHandlingConfig { DuplicatePolicy = duplicatePolicy },
            });
            _configuration.SavePhotoFiling(new PhotoFilingSettings { SettleSeconds = settleSeconds });
            var spaces = new ViewPersonalSpaceRepository(_database);
            var storage = new ViewStorageService(_configuration, spaces, new ViewSharedLibraryRepository(_database));
            Intake = new ViewIntakeRepository(_database);
            Clock = new StepClock();
            Areas = new ViewIntakeAreas(_configuration, new ProfileRepository(_database));
            var library = new ViewLibraryService(Repository, _configuration, spaces, storage, NullLogger<ViewLibraryService>.Instance);
            TidyRepository = new ViewTidyRepository(_database);
            Filer = new ViewIntakeFiler(Areas, Intake, library, storage, Repository, _configuration, Clock,
                NullLogger<ViewIntakeFiler>.Instance, tidy: TidyRepository);
            Storage = storage;
            Spaces = spaces;
            Duplicates = new ViewDuplicatesService(Intake, Repository, _configuration, Clock);
            Tidy = new ViewTidyService(storage, library, new ViewSharedLibraryRepository(_database), TidyRepository, Intake, _configuration, Clock,
                NullLogger<ViewTidyService>.Instance);
            Areas.EnsureFolders();
        }

        public ViewDuplicatesService Duplicates { get; }
        public ViewTidyService Tidy { get; }
        public ViewTidyRepository TidyRepository { get; }

        public void SetLayout(string pattern) =>
            _configuration.SavePhotoFiling(new PhotoFilingSettings { SettleSeconds = 0, LayoutPattern = pattern });

        /// <summary>The managed photos folder the filer created in the owner's space.</summary>
        public async Task<Guid> FolderSourceIdAsync()
        {
            var space = (await Spaces.GetByOwnerAsync(OwnerProfileId))!;
            return (await Spaces.GetSourcesAsync(space.Id))
                .First(source => source.StorageMode == ViewSourceStorageMode.Managed && source.ContentKind == ViewContentKinds.Photos).Id;
        }

        /// <summary>Plants a second item with exactly the first item's file, as older data or a past bug could have left behind.</summary>
        public void CopyFirstItem()
        {
            using var connection = _database.CreateConnection();
            var first = connection.QueryFirst<Guid>("SELECT id FROM local_items ORDER BY created_at LIMIT 1;");
            var copy = Guid.NewGuid();
            connection.Execute("""
                INSERT INTO local_items (id, scope_kind, personal_space_id, owner_profile_id, library_id, media_kind, title, primary_file_name,
                    primary_mime_type, captured_at, embedded_captured_at, captured_at_user_override, created_at, updated_at, favorite, hidden,
                    archived_at, trashed_at, content_kind, added_by_profile_id)
                SELECT @copy, scope_kind, personal_space_id, owner_profile_id, library_id, media_kind, title, 'copy-' || primary_file_name,
                    primary_mime_type, captured_at, embedded_captured_at, captured_at_user_override, '2999-01-01T00:00:00.0000000+00:00', updated_at,
                    favorite, hidden, archived_at, trashed_at, content_kind, added_by_profile_id
                  FROM local_items WHERE id = @first;
                INSERT INTO local_item_files (item_id, file_id, role, derivative_kind, position, added_at)
                SELECT @copy, file_id, role, derivative_kind, position, added_at FROM local_item_files WHERE item_id = @first;
                """, new { copy, first });
        }
        public Guid OwnerProfileId { get; }
        public LocalAssetRepository Repository { get; }
        public ViewIntakeRepository Intake { get; }
        public ViewIntakeAreas Areas { get; }
        public ViewIntakeFiler Filer { get; }
        public StepClock Clock { get; }
        public ViewStorageService Storage { get; }
        public ViewPersonalSpaceRepository Spaces { get; }

        /// <summary>The owner's managed intake folder, where filed photos land.</summary>
        public string LibraryFolder
        {
            get
            {
                var space = Storage.EnsurePersonalSpaceAsync(OwnerProfileId).GetAwaiter().GetResult();
                var source = Storage.EnsureManagedSourceAsync(
                    space, "Browser uploads", ViewSourceType.BrowserUpload, ViewKindAcceptance.BrowserUploadSourceKey).GetAwaiter().GetResult();
                return Storage.GetSourcePath(space, source);
            }
        }

        public string SectionPath(string kind) => Areas.SectionPath(kind)!;

        public async Task<Guid> AddProfileAsync(string name)
        {
            var id = Guid.NewGuid();
            await ProfileTestData.InsertAsync(_database, new Profile
            {
                Id = id,
                DisplayName = name,
                Role = ProfileRole.RestrictedProfile,
                CreatedAt = new DateTimeOffset(2021, 1, 1, 0, 0, 0, TimeSpan.Zero),
            });
            return id;
        }

        public string Drop(string kind, string relativePath, byte[] bytes)
        {
            var path = Path.Combine(SectionPath(kind), relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        /// <summary>A tiny JPEG that starts and ends correctly; <paramref name="seed"/> makes its content (and so its hash) different.</summary>
        public static byte[] Jpeg(int seed, int extra = 0)
        {
            var bytes = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0 };
            bytes.AddRange(Enumerable.Range(0, 32 + extra).Select(i => (byte)((i * 7 + seed) & 0x7F)));
            bytes.AddRange([0xFF, 0xD9]);
            return [.. bytes];
        }

        public void Dispose()
        {
            _configuration.Dispose();
            _database.Dispose();
            TestTemp.ReleaseSqlitePoolsIn(_root);
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
