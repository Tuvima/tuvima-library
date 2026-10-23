using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.FileWrites;
using MediaEngine.Api.Services.Libraries;
using MediaEngine.Api.Services.Libraries.Moves;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Api.Services.Settings;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Organise;
using MediaEngine.Intelligence;
using MediaEngine.Storage;
using MediaEngine.Storage.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Account = MediaEngine.Domain.Entities.Account;
using AccountProfileGrant = MediaEngine.Domain.Entities.AccountProfileGrant;

namespace MediaEngine.Api.Tests;

/// <summary>
/// "Change what this folder is" (Library setup spec D40(c), D60 to D64, D69) end to end on real temp folders, a real data store and the real
/// worker: the folder moves to the new kind's library, each file is placed, has its details dropped or is identified again, and its filing
/// waits for the batch. The tag writer, the YouTube re-read and the lookup pipeline are not run; the durable records of what they would be
/// asked to do are checked.
/// </summary>
public sealed class FolderKindChangeTests : IDisposable
{
    private readonly World _w = new();

    public void Dispose() => _w.Dispose();

    // ----- the behaviour -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_Managed_Movies_folder_changed_to_Videos_moves_its_files_drops_their_details_queues_one_filing_move_each_and_keeps_progress_and_reactions()
    {
        var folder = _w.AddFolder(_w.MoviesLibrary, "Home", managed: true);
        var first = _w.AddFile(folder, "first.mkv", MediaType.Movies, "First clip");
        var second = _w.AddFile(folder, "second.mkv", MediaType.Movies, "Second clip");
        _w.Execute("""
            INSERT INTO user_states (user_id, asset_id, progress_pct, last_accessed) VALUES (@person, @asset, 40, '2026-10-01T00:00:00Z');
            INSERT INTO profile_reactions (profile_id, entity_kind, entity_id, reaction, updated_at) VALUES (@person, 'Movie', @work, 'Love', '2026-10-01T00:00:00Z');
            """, new { person = Profile.SeedProfileId, asset = first.AssetId, work = first.WorkId });

        var status = await _w.Service.StartAsync(folder.Id, new() { KindKey = "other_videos", LibraryId = _w.VideosLibrary }, default);
        await _w.RunWorkerUntilFinishedAsync(status.OperationId);

        var done = await _w.Jobs.GetAsync(Guid.Parse(status.OperationId), default);
        Assert.Equal("change_folder_kind", status.JobType);
        Assert.Equal("completed", done!.State);
        Assert.Equal(2, done.ItemsCompleted);
        Assert.Equal("2 files changed from Movies to Videos.", done.Message);

        // The folder, with its management mode, is in the Videos library; the old library stays.
        var libraries = _w.Config.LoadLibraries();
        var moved = libraries.Libraries.Single(library => library.Id == _w.VideosLibrary).Sources.Single(source => source.Id == folder.Id);
        Assert.Equal(LibrarySourceManagementModes.ManagedByTuvima, moved.ManagementMode);
        Assert.Empty(libraries.Libraries.Single(library => library.Id == _w.MoviesLibrary).Sources);

        // Each file moved with it, as Videos, and its looked-up details are gone while the file's own title shows.
        Assert.All([first, second], file =>
        {
            Assert.Equal(_w.VideosLibrary, _w.Scalar<string>("SELECT library_id FROM media_assets WHERE id = @id;", new { id = file.AssetId }));
            Assert.Equal("Videos", _w.Scalar<string>(
                "SELECT w.media_type FROM media_assets ma JOIN editions e ON e.id = ma.edition_id JOIN works w ON w.id = e.work_id WHERE ma.id = @id;", new { id = file.AssetId }));
            Assert.Equal(0, _w.Scalar<int>("SELECT COUNT(*) FROM metadata_claims WHERE entity_id = @id AND provider_id = @tmdb;", new { id = file.AssetId, tmdb = WellKnownProviders.Tmdb }));
            Assert.Equal(file.Title, _w.Scalar<string>("SELECT value FROM canonical_values WHERE entity_id = @id AND key = 'title';", new { id = file.AssetId }));
            Assert.True(File.Exists(file.Path));
        });
        Assert.Equal(0, _w.Scalar<int>("SELECT COUNT(*) FROM works WHERE media_type = 'Movies';"));

        // One filing move each, waiting for the batch (nothing moved on disk).
        Assert.Equal(
            [("filing", "waiting"), ("filing", "waiting")],
            _w.Rows<(string, string)>("SELECT move_origin, move_step FROM media_file_write_intents ORDER BY asset_id;"));
        Assert.Equal(2, _w.Filing.Requests.Count);
        Assert.All(_w.Filing.Requests, request => Assert.False(request.Held));

        // Watch progress stays with the file; the reaction followed the file's title to its new work.
        Assert.Equal(40, _w.Scalar<int>("SELECT progress_pct FROM user_states WHERE asset_id = @id;", new { id = first.AssetId }));
        var newWork = _w.Scalar<Guid>("SELECT e.work_id FROM media_assets ma JOIN editions e ON e.id = ma.edition_id WHERE ma.id = @id;", new { id = first.AssetId });
        Assert.Equal(newWork, _w.Scalar<Guid>("SELECT entity_id FROM profile_reactions WHERE profile_id = @person;", new { person = Profile.SeedProfileId }));
        Assert.Equal("Love", _w.Scalar<string>("SELECT reaction FROM profile_reactions WHERE profile_id = @person;", new { person = Profile.SeedProfileId }));

        // A plain kind has no first check; the job is in the ledger and in the administrator's activity.
        Assert.Null(await _w.FirstChecks.GetAsync(folder.Id));
        Assert.Equal(2, await _w.Ledger.CountAsync(Guid.Parse(status.OperationId)));
        Assert.Contains(
            await _w.Activity.GetRecentAsync(20),
            entry => entry.ActionType == SystemActionType.FolderKindChanged && entry.Detail == "Home changed from Movies to Videos: 2 files");
    }

    [Fact]
    public async Task A_file_changed_from_Movies_to_Videos_with_an_embedded_title_still_gets_a_refile_plan()
    {
        using var files = new MoveFileWorld(_w.Database, _w.MediaRoot);
        // The naming templates the Engine ships in config/core.json for these two types.
        files.Options.OrganizationTemplates["Movies"] = "Movies/{Title} ({Year})/{Title} ({Year}){Ext}";
        files.Options.OrganizationTemplates["Videos"] = "Videos/{Title}{Ext}";
        var folder = _w.AddFolder(_w.MoviesLibrary, "Managed", managed: true, path: files.Managed);
        var file = _w.AddFile(folder, "clip.mkv", MediaType.Movies, "Clip", year: "2021");

        var status = await _w.Service.StartAsync(folder.Id, new() { KindKey = "other_videos", LibraryId = _w.VideosLibrary }, default);
        await _w.RunWorkerUntilFinishedAsync(status.OperationId);

        // The looked-up details are gone, yet the file's own claims were scored again, so the organiser still has a name to take.
        Assert.Equal(0, _w.Scalar<int>("SELECT COUNT(*) FROM metadata_claims WHERE entity_id = @id AND provider_id = @tmdb;", new { id = file.AssetId, tmdb = WellKnownProviders.Tmdb }));
        var plan = await files.Organizer.PlanRefileAsync(file.AssetId, null);
        Assert.True(AssetRefileDisposition.Moves == plan.Disposition, plan.Reason);
        // The Videos pattern (the media type after the file was placed again), named by the file's own title.
        Assert.Equal(Path.Combine(files.Managed, "Videos", "Clip.mkv"), plan.TargetPath);
    }

    [Fact]
    public async Task A_Read_only_Music_folder_changed_to_Other_audio_changes_the_library_and_drops_the_details_but_nothing_moves()
    {
        var folder = _w.AddFolder(_w.MusicLibrary, "Songs", managed: false);
        var song = _w.AddFile(folder, "song.mp3", MediaType.Music, "My song");

        var status = await _w.Service.StartAsync(folder.Id, new() { KindKey = "other_audio", LibraryId = _w.AudioLibrary }, default);
        await _w.RunWorkerUntilFinishedAsync(status.OperationId);

        Assert.Equal(_w.AudioLibrary, _w.Scalar<string>("SELECT library_id FROM media_assets WHERE id = @id;", new { id = song.AssetId }));
        // The media type did not change, so the file stays under the Work it had.
        Assert.Equal(song.WorkId, _w.Scalar<Guid>("SELECT e.work_id FROM media_assets ma JOIN editions e ON e.id = ma.edition_id WHERE ma.id = @id;", new { id = song.AssetId }));
        Assert.Equal(0, _w.Scalar<int>("SELECT COUNT(*) FROM metadata_claims WHERE provider_id = @tmdb;", new { tmdb = WellKnownProviders.Tmdb }));
        Assert.Equal("My song", _w.Scalar<string>("SELECT value FROM canonical_values WHERE entity_id = @id AND key = 'title';", new { id = song.AssetId }));
        Assert.Equal(0, _w.Scalar<int>("SELECT COUNT(*) FROM media_file_write_intents;"));
        Assert.Empty(_w.Filing.Requests);
        Assert.True(File.Exists(song.Path));
        Assert.Equal(LibrarySourceManagementModes.ExistingLibrary,
            _w.Config.LoadLibraries().Libraries.Single(library => library.Id == _w.AudioLibrary).Sources.Single().ManagementMode);
    }

    [Fact]
    public async Task An_Other_videos_folder_changed_to_YouTube_starts_a_first_check_holds_filing_and_reads_each_file_again()
    {
        var folder = _w.AddFolder(_w.VideosLibrary, "Clips", managed: true);
        var one = _w.AddFile(folder, "one.mkv", MediaType.Videos, "One");
        var two = _w.AddFile(folder, "two.mkv", MediaType.Videos, "Two");

        var preview = await _w.Service.PreviewAsync(folder.Id, new() { KindKey = "youtube" }, default);
        var status = await _w.Service.StartAsync(folder.Id, new() { KindKey = "youtube" }, default);
        await _w.RunWorkerUntilFinishedAsync(status.OperationId);

        Assert.True(preview.StartsFirstCheck);
        Assert.False(preview.DropsDetails);
        var check = await _w.FirstChecks.GetAsync(folder.Id);
        Assert.Equal(("checking", _w.YouTubeLibrary), (check!.State, check.LibraryId));
        Assert.Equal(_w.YouTubeLibrary, _w.Scalar<string>("SELECT library_id FROM media_assets WHERE id = @id;", new { id = one.AssetId }));
        // The files are read again under the YouTube library (the lookup runs on the video ID).
        Assert.Equivalent(new[] { one.AssetId, two.AssetId }, _w.Rereader.Assets.ToArray());
        // Moves wait until the check passes.
        Assert.Equal(
            [("filing", "held"), ("filing", "held")],
            _w.Rows<(string, string)>("SELECT move_origin, move_step FROM media_file_write_intents ORDER BY asset_id;"));
        Assert.All(_w.Filing.Requests, request => Assert.True(request.Held));
    }

    [Fact]
    public async Task A_folder_changed_to_a_kind_that_looks_things_up_queues_a_new_identity_job_under_the_new_media_type()
    {
        var folder = _w.AddFolder(_w.VideosLibrary, "Films", managed: false);
        var file = _w.AddFile(folder, "film.mkv", MediaType.Videos, "A film");

        var status = await _w.Service.StartAsync(folder.Id, new() { KindKey = "movies" }, default);
        await _w.RunWorkerUntilFinishedAsync(status.OperationId);

        var job = _w.Rows<(string EntityType, string MediaType, string Pass, string State)>(
            "SELECT entity_type, media_type, pass, state FROM identity_jobs WHERE entity_id = @id;", new { id = file.AssetId }).Single();
        Assert.Equal(("MediaAsset", "Movies", "Quick", "Queued"), job);
        Assert.Equal("Movies", _w.Scalar<string>(
            "SELECT w.media_type FROM media_assets ma JOIN editions e ON e.id = ma.edition_id JOIN works w ON w.id = e.work_id WHERE ma.id = @id;", new { id = file.AssetId }));
        Assert.Equal("checking", (await _w.FirstChecks.GetAsync(folder.Id))!.State);
        Assert.Equal(0, _w.Scalar<int>("SELECT COUNT(*) FROM media_file_write_intents;"));
    }

    [Fact]
    public async Task A_folder_with_a_first_check_running_ends_it_when_it_changes_kind_and_a_plain_target_starts_none()
    {
        var folder = _w.AddFolder(_w.MoviesLibrary, "Films", managed: true);
        _w.AddFile(folder, "film.mkv", MediaType.Movies, "A film");
        await _w.FirstChecks.StartAsync(_w.MoviesLibrary, folder.Id, folder.Path, DateTimeOffset.UtcNow);

        var status = await _w.Service.StartAsync(folder.Id, new() { KindKey = "other_videos", LibraryId = _w.VideosLibrary }, default);
        await _w.RunWorkerUntilFinishedAsync(status.OperationId);

        Assert.Null(await _w.FirstChecks.GetAsync(folder.Id));
    }

    // ----- crash safety ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_job_stopped_halfway_resumes_without_repeating_finished_files_or_the_folder_move()
    {
        var folder = _w.AddFolder(_w.VideosLibrary, "Clips", managed: true);
        var files = Enumerable.Range(0, 4).Select(index => _w.AddFile(folder, $"clip{index}.mkv", MediaType.Videos, $"Clip {index}")).ToList();
        var status = await _w.Service.StartAsync(folder.Id, new() { KindKey = "youtube" }, default);
        var operationId = Guid.Parse(status.OperationId);

        // The first run is stopped after two files (a pause, or the Engine stopping).
        var stopped = await _w.Runner.RunAsync(operationId, Guid.Parse(folder.Id), new ScriptedHost(allowFiles: 2), default);
        Assert.Equal(FolderOrganiseStop.Paused, stopped.Stop);
        Assert.Equal(2, _w.Filing.Requests.Count);
        Assert.Equal(2, (await _w.Ledger.GetFinishedPathsAsync(operationId)).Count);
        var checkStartedAt = (await _w.FirstChecks.GetAsync(folder.Id))!.StartedAt;

        var resumed = await _w.Runner.RunAsync(operationId, Guid.Parse(folder.Id), new ScriptedHost(allowFiles: 100), default);

        Assert.Equal(FolderOrganiseStop.Finished, resumed.Stop);
        Assert.Equal(4, resumed.Count(FolderOrganiseOutcomes.KindChanged));
        // Each file was changed exactly once; the folder move and the first check were not done a second time.
        Assert.Equal(4, _w.Filing.Requests.Count);
        Assert.Equal(files.Select(file => file.AssetId).Order(), _w.Filing.Requests.Select(request => request.AssetId).Order());
        Assert.Equal(4, _w.Rereader.Assets.Count);
        Assert.Equal(checkStartedAt, (await _w.FirstChecks.GetAsync(folder.Id))!.StartedAt);
        Assert.Single(_w.Config.LoadLibraries().Libraries.SelectMany(library => library.Sources), source => source.Id == folder.Id);
    }

    [Fact]
    public async Task A_change_left_running_when_the_Engine_stopped_resumes_when_it_starts_again()
    {
        var folder = _w.AddFolder(_w.VideosLibrary, "Clips", managed: false);
        var file = _w.AddFile(folder, "clip.mkv", MediaType.Videos, "Clip");
        var status = await _w.Service.StartAsync(folder.Id, new() { KindKey = "youtube" }, default);
        // The worker had leased the job when the Engine stopped.
        var leased = await _w.Operations.LeaseNextAsync("earlier-start", [MediaOperationType.ChangeFolderKind], 1, TimeSpan.FromMinutes(10));
        Assert.Equal(status.OperationId, leased.Single().Id.ToString("D"));

        await _w.RunWorkerUntilFinishedAsync(status.OperationId);

        Assert.Equal(_w.YouTubeLibrary, _w.Scalar<string>("SELECT library_id FROM media_assets WHERE id = @id;", new { id = file.AssetId }));
    }

    [Fact]
    public async Task A_change_on_a_Read_only_folder_is_not_stopped_because_the_folder_is_not_managed()
    {
        var folder = _w.AddFolder(_w.MoviesLibrary, "Films", managed: false);
        var status = await _w.Service.StartAsync(folder.Id, new() { KindKey = "other_videos", LibraryId = _w.VideosLibrary }, default);

        await _w.Jobs.StopJobsForUnmanagedFoldersAsync(_w.Config.LoadLibraries(), default);

        Assert.Equal("queued", (await _w.Jobs.GetAsync(Guid.Parse(status.OperationId), default))!.State);
    }

    [Fact]
    public async Task A_second_change_while_one_is_open_is_refused()
    {
        var folder = _w.AddFolder(_w.MoviesLibrary, "Films", managed: false);
        _w.AddFile(folder, "film.mkv", MediaType.Movies, "A film");
        await _w.Service.StartAsync(folder.Id, new() { KindKey = "other_videos", LibraryId = _w.VideosLibrary }, default);

        var again = await Assert.ThrowsAsync<FolderJobException>(() => _w.Service.StartAsync(folder.Id, new() { KindKey = "other_videos", LibraryId = _w.VideosLibrary }, default));
        var preview = await Assert.ThrowsAsync<FolderJobException>(() => _w.Service.PreviewAsync(folder.Id, new() { KindKey = "other_videos", LibraryId = _w.VideosLibrary }, default));
        var convert = await Assert.ThrowsAsync<FolderJobException>(() => _w.Jobs.StartConversionAsync(folder.Id, default));

        Assert.True(again.Conflict);
        Assert.True(preview.Conflict);
        Assert.True(convert.Conflict);
        Assert.Single(await _w.Operations.GetQueueAsync(MediaOperationQueue.FolderJobs, 50));
    }

    [Fact]
    public async Task A_change_that_was_cancelled_half_way_can_be_started_again_to_finish_the_files_not_yet_changed()
    {
        var folder = _w.AddFolder(_w.VideosLibrary, "Clips", managed: false);
        var files = Enumerable.Range(0, 3).Select(index => _w.AddFile(folder, $"clip{index}.mkv", MediaType.Videos, $"Clip {index}")).ToList();
        var first = await _w.Service.StartAsync(folder.Id, new() { KindKey = "youtube" }, default);
        await _w.Runner.RunAsync(Guid.Parse(first.OperationId), Guid.Parse(folder.Id), new ScriptedHost(allowFiles: 1), default);
        await _w.Operations.MarkCancelledAsync(Guid.Parse(first.OperationId), "Stopped.");

        // The folder is already a YouTube folder, but two files are still registered in the old library.
        var preview = await _w.Service.PreviewAsync(folder.Id, new() { KindKey = "youtube" }, default);
        var second = await _w.Service.StartAsync(folder.Id, new() { KindKey = "youtube" }, default);
        await _w.RunWorkerUntilFinishedAsync(second.OperationId);

        Assert.Equal(2, preview.FileCount);
        Assert.All(files, file => Assert.Equal(_w.YouTubeLibrary, _w.Scalar<string>("SELECT library_id FROM media_assets WHERE id = @id;", new { id = file.AssetId })));
        Assert.Equal(3, _w.Rereader.Assets.Distinct().Count());
    }

    [Fact]
    public async Task A_file_that_cannot_be_changed_stays_in_the_old_library_the_job_says_it_finished_with_it_not_changed_and_starting_again_finishes_it()
    {
        var folder = _w.AddFolder(_w.VideosLibrary, "Clips", managed: false);
        var bad = _w.AddFile(folder, "bad.mkv", MediaType.Videos, "Bad");
        var good = _w.AddFile(folder, "good.mkv", MediaType.Videos, "Good");
        _w.Rereader.FailFor = bad.AssetId;

        var status = await _w.Service.StartAsync(folder.Id, new() { KindKey = "youtube" }, default);
        await _w.RunWorkerUntilFinishedAsync(status.OperationId);

        // Not a plain success, and the file is still wholly what it was.
        var done = await _w.Jobs.GetAsync(Guid.Parse(status.OperationId), default);
        Assert.Equal("failed", done!.State);
        Assert.Equal(1, done.ItemsFailed);
        Assert.StartsWith("Finished, but 1 file was not changed (1 file changed from Videos to YouTube).", done.Message, StringComparison.Ordinal);
        Assert.Equal(_w.VideosLibrary, _w.Scalar<string>("SELECT library_id FROM media_assets WHERE id = @id;", new { id = bad.AssetId }));
        Assert.Equal(_w.YouTubeLibrary, _w.Scalar<string>("SELECT library_id FROM media_assets WHERE id = @id;", new { id = good.AssetId }));
        var log = await _w.Jobs.GetLogAsync(Guid.Parse(status.OperationId), 0, 50, default);
        Assert.Equal(
            [(bad.Path, "failed"), (good.Path, "kind_changed")],
            log.Entries.Select(entry => (entry.OldPath, entry.Outcome)).OrderBy(pair => pair.OldPath, StringComparer.Ordinal));

        // The same kind again (the cause is gone) picks up only the file that failed.
        _w.Rereader.FailFor = null;
        var rereadsBefore = _w.Rereader.Assets.Count;
        var preview = await _w.Service.PreviewAsync(folder.Id, new() { KindKey = "youtube" }, default);
        var repair = await _w.Service.StartAsync(folder.Id, new() { KindKey = "youtube" }, default);
        await _w.RunWorkerUntilFinishedAsync(repair.OperationId);

        Assert.Equal(1, preview.FileCount);
        Assert.Equal(_w.YouTubeLibrary, _w.Scalar<string>("SELECT library_id FROM media_assets WHERE id = @id;", new { id = bad.AssetId }));
        Assert.Equal("completed", (await _w.Jobs.GetAsync(Guid.Parse(repair.OperationId), default))!.State);
        Assert.Equal([bad.AssetId], _w.Rereader.Assets.Skip(rereadsBefore).ToArray());
        // The folder move and its first check were done once, by the first job.
        Assert.Single(_w.Config.LoadLibraries().Libraries.SelectMany(library => library.Sources), source => source.Id == folder.Id);
    }

    [Fact]
    public async Task A_file_in_a_nested_folder_that_belongs_to_another_library_is_left_alone_even_when_an_earlier_change_listed_it_as_not_finished()
    {
        var folder = _w.AddFolder(_w.VideosLibrary, "Clips", managed: false);
        var bad = _w.AddFile(folder, "bad.mkv", MediaType.Videos, "Bad");
        _w.Rereader.FailFor = bad.AssetId;
        var first = await _w.Service.StartAsync(folder.Id, new() { KindKey = "youtube" }, default);
        await _w.RunWorkerUntilFinishedAsync(first.OperationId);

        // A folder inside this one is registered to the TV library, and an earlier plan's ledger lists one of its files as not finished.
        var kids = _w.AddFolder(_w.TvLibrary, "Kids", managed: false);
        var stray = _w.AddFile(kids, "episode.mkv", MediaType.TV, "An episode");
        var nestedPath = Path.Combine(folder.Path, "Kids", "episode.mkv");
        _w.Execute("UPDATE media_assets SET file_path_root = @path WHERE id = @id;", new { path = nestedPath, id = stray.AssetId });
        await _w.Ledger.UpsertAsync(new FolderOrganiseLogEntry(
            Guid.Parse(first.OperationId), Guid.Parse(folder.Id), nestedPath, nestedPath, FolderOrganiseOutcomes.Failed, DateTimeOffset.UtcNow));
        _w.Rereader.FailFor = null;

        // The folder's own count and the files the next run takes leave it out.
        var preview = await _w.Service.PreviewAsync(folder.Id, new() { KindKey = "youtube" }, default);
        var repair = await _w.Service.StartAsync(folder.Id, new() { KindKey = "youtube" }, default);
        await _w.RunWorkerUntilFinishedAsync(repair.OperationId);
        var back = await _w.Service.StartAsync(folder.Id, new() { KindKey = "other_videos", LibraryId = _w.VideosLibrary }, default);
        await _w.RunWorkerUntilFinishedAsync(back.OperationId);

        Assert.Equal(1, preview.FileCount);
        Assert.Equal(_w.TvLibrary, _w.Scalar<string>("SELECT library_id FROM media_assets WHERE id = @id;", new { id = stray.AssetId }));
        Assert.Equal("TV", WorkTypeOf(stray));
        Assert.Equal(_w.VideosLibrary, _w.Scalar<string>("SELECT library_id FROM media_assets WHERE id = @id;", new { id = bad.AssetId }));
        Assert.Null(await _w.Ledger.GetAsync(Guid.Parse(back.OperationId), nestedPath));
    }

    [Fact]
    public async Task A_different_kind_is_refused_while_an_earlier_change_of_the_folder_has_files_not_changed()
    {
        var folder = _w.AddFolder(_w.VideosLibrary, "Clips", managed: false);
        var bad = _w.AddFile(folder, "bad.mkv", MediaType.Videos, "Bad");
        _w.Rereader.FailFor = bad.AssetId;
        var status = await _w.Service.StartAsync(folder.Id, new() { KindKey = "youtube" }, default);
        await _w.RunWorkerUntilFinishedAsync(status.OperationId);

        // A third kind waits (the original kind, Videos, would be allowed: changing back).
        var refused = await Assert.ThrowsAsync<FolderJobException>(() => _w.Service.StartAsync(folder.Id, new() { KindKey = "movies" }, default));

        Assert.Contains("not finished", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_replace_that_fails_once_leaves_the_file_unchanged_and_is_retried_when_the_job_resumes()
    {
        var folder = _w.AddFolder(_w.MoviesLibrary, "Films", managed: true);
        var file = _w.AddFile(folder, "film.mkv", MediaType.Movies, "A film");
        var status = await _w.Service.StartAsync(folder.Id, new() { KindKey = "other_videos", LibraryId = _w.VideosLibrary }, default);
        var operationId = Guid.Parse(status.OperationId);
        _w.FlakyChanges.ReplaceFailures = 1;

        var first = await _w.Runner.RunAsync(operationId, Guid.Parse(folder.Id), new ScriptedHost(allowFiles: 10), default);

        // Nothing of the file changed: it is in its old library, under its old work, with no filing queued, and the ledger says failed.
        Assert.Equal((FolderOrganiseStop.Finished, 1), (first.Stop, first.Failed));
        Assert.Equal(_w.MoviesLibrary, _w.Scalar<string>("SELECT library_id FROM media_assets WHERE id = @id;", new { id = file.AssetId }));
        Assert.Equal("Movies", _w.Scalar<string>("SELECT w.media_type FROM media_assets ma JOIN editions e ON e.id = ma.edition_id JOIN works w ON w.id = e.work_id WHERE ma.id = @id;", new { id = file.AssetId }));
        Assert.Empty(_w.Filing.Requests);
        Assert.Equal("failed", (await _w.Ledger.GetAsync(operationId, file.Path))!.Outcome);

        var second = await _w.Runner.RunAsync(operationId, Guid.Parse(folder.Id), new ScriptedHost(allowFiles: 10), default);

        Assert.Equal((FolderOrganiseStop.Finished, 0), (second.Stop, second.Failed));
        Assert.Equal(_w.VideosLibrary, _w.Scalar<string>("SELECT library_id FROM media_assets WHERE id = @id;", new { id = file.AssetId }));
        Assert.Equal("kind_changed", (await _w.Ledger.GetAsync(operationId, file.Path))!.Outcome);
        Assert.Single(_w.Filing.Requests);
    }

    [Fact]
    public async Task A_file_and_an_existing_title_that_both_hold_a_persons_own_details_is_refused_counted_in_the_message_and_never_retried_in_a_loop()
    {
        var folder = _w.AddFolder(_w.MoviesLibrary, "Films", managed: false);
        var file = _w.AddFile(folder, "film.mkv", MediaType.Movies, "A film");
        var hints = new Dictionary<string, string>
        {
            ["show_name"] = "Some Show",
            [MetadataFieldConstants.SeasonNumber] = "1",
            [MetadataFieldConstants.EpisodeNumber] = "1",
        };
        var episode = (await new HierarchyResolver(new WorkRepository(_w.Database)).ResolveAsync(MediaType.TV, hints)).WorkId;
        _w.Execute("""
            UPDATE works SET display_overrides_json = '{"genre":"Comedy"}' WHERE id = @episode;
            INSERT INTO entity_assets (id, entity_id, entity_type, asset_type, source_provider, is_user_override) VALUES (@asset, @work, 'Work', 'CoverArt', 'user_upload', 1);
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES
                (@file, 'show_name', 'Some Show', '2026-01-01'), (@file, 'season_number', '1', '2026-01-01'), (@file, 'episode_number', '1', '2026-01-01'),
                (@work, 'title', 'A film', '2026-01-01'), (@episode, 'title', 'Pilot', '2026-01-01');
            """, new { episode, asset = Guid.NewGuid(), work = file.WorkId, file = file.AssetId });

        var status = await _w.Service.StartAsync(folder.Id, new() { KindKey = "tv" }, default);
        var operationId = Guid.Parse(status.OperationId);
        await _w.RunWorkerUntilFinishedAsync(status.OperationId);

        var done = await _w.Jobs.GetAsync(operationId, default);
        Assert.Equal("failed", done!.State);
        Assert.Contains("\"A film\" and \"Pilot\" both have your own artwork or details. Remove them from one of the two in Editing Mode, then start the change again.", done.Message, StringComparison.Ordinal);
        Assert.StartsWith("Finished, but 1 file was not changed", done.Message, StringComparison.Ordinal);
        Assert.Contains("Pilot", (await _w.Ledger.GetAsync(operationId, file.Path))!.Detail, StringComparison.Ordinal);
        Assert.Equal("kind_change_refused", (await _w.Ledger.GetAsync(operationId, file.Path))!.Outcome);
        Assert.Equal(_w.MoviesLibrary, _w.Scalar<string>("SELECT library_id FROM media_assets WHERE id = @id;", new { id = file.AssetId }));
        Assert.Equal(file.WorkId, _w.Scalar<Guid>("SELECT e.work_id FROM media_assets ma JOIN editions e ON e.id = ma.edition_id WHERE ma.id = @id;", new { id = file.AssetId }));
        Assert.Equal(1, _w.FlakyChanges.ReplaceCalls);

        // Resuming the same job does not try it again.
        await _w.Runner.RunAsync(operationId, Guid.Parse(folder.Id), new ScriptedHost(allowFiles: 10), default);
        Assert.Equal(1, _w.FlakyChanges.ReplaceCalls);

        // Starting the same kind again looks at it once more, refuses it once more, and ends.
        var again = await _w.Service.StartAsync(folder.Id, new() { KindKey = "tv" }, default);
        await _w.RunWorkerUntilFinishedAsync(again.OperationId);
        Assert.Equal(2, _w.FlakyChanges.ReplaceCalls);
        Assert.Equal("failed", (await _w.Jobs.GetAsync(Guid.Parse(again.OperationId), default))!.State);
    }

    [Fact]
    public async Task After_a_clash_the_folder_can_be_changed_back_and_ends_whole_in_its_original_kind_while_a_third_kind_is_refused_naming_both_titles()
    {
        var folder = _w.AddFolder(_w.MoviesLibrary, "Films", managed: false);
        var clash = _w.AddFile(folder, "clash.mkv", MediaType.Movies, "Clash film");
        var fine = _w.AddFile(folder, "fine.mkv", MediaType.Movies, "Fine film");
        var episode = (await new HierarchyResolver(new WorkRepository(_w.Database)).ResolveAsync(MediaType.TV, new Dictionary<string, string>
        {
            ["show_name"] = "Some Show",
            [MetadataFieldConstants.SeasonNumber] = "1",
            [MetadataFieldConstants.EpisodeNumber] = "1",
        })).WorkId;
        _w.Execute("""
            UPDATE works SET display_overrides_json = '{"genre":"Comedy"}' WHERE id = @episode;
            INSERT INTO entity_assets (id, entity_id, entity_type, asset_type, source_provider, is_user_override) VALUES (@asset, @work, 'Work', 'CoverArt', 'user_upload', 1);
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES
                (@clash, 'show_name', 'Some Show', '2026-01-01'), (@clash, 'season_number', '1', '2026-01-01'), (@clash, 'episode_number', '1', '2026-01-01'),
                (@work, 'title', 'Clash film', '2026-01-01'), (@episode, 'title', 'Pilot', '2026-01-01');
            """, new { episode, asset = Guid.NewGuid(), work = clash.WorkId, clash = clash.AssetId });
        var forward = await _w.Service.StartAsync(folder.Id, new() { KindKey = "tv" }, default);
        await _w.RunWorkerUntilFinishedAsync(forward.OperationId);
        Assert.Equal(_w.TvLibrary, _w.Scalar<string>("SELECT library_id FROM media_assets WHERE id = @id;", new { id = fine.AssetId }));
        Assert.Equal(_w.MoviesLibrary, _w.Scalar<string>("SELECT library_id FROM media_assets WHERE id = @id;", new { id = clash.AssetId }));

        // Any other kind waits, and says which two titles are in the way and what to do.
        var third = await Assert.ThrowsAsync<FolderJobException>(() => _w.Service.StartAsync(folder.Id, new() { KindKey = "other_videos", LibraryId = _w.VideosLibrary }, default));
        Assert.Contains("\"Clash film\" and \"Pilot\" both have your own artwork or details", third.Message, StringComparison.Ordinal);
        Assert.Contains("Editing Mode", third.Message, StringComparison.Ordinal);

        // Changing back is allowed: the file that was changed goes back, the one left behind is already there.
        var preview = await _w.Service.PreviewAsync(folder.Id, new() { KindKey = "movies" }, default);
        var back = await _w.Service.StartAsync(folder.Id, new() { KindKey = "movies" }, default);
        await _w.RunWorkerUntilFinishedAsync(back.OperationId);

        Assert.Equal(1, preview.FileCount);
        Assert.Equal("completed", (await _w.Jobs.GetAsync(Guid.Parse(back.OperationId), default))!.State);
        Assert.All([clash, fine], file => Assert.Equal(_w.MoviesLibrary, _w.Scalar<string>("SELECT library_id FROM media_assets WHERE id = @id;", new { id = file.AssetId })));
        Assert.Equal("Movies", _w.Scalar<string>("SELECT w.media_type FROM media_assets ma JOIN editions e ON e.id = ma.edition_id JOIN works w ON w.id = e.work_id WHERE ma.id = @id;", new { id = fine.AssetId }));
        var libraries = _w.Config.LoadLibraries();
        Assert.Single(libraries.Libraries.Single(library => library.Id == _w.MoviesLibrary).Sources, source => source.Id == folder.Id);
        Assert.Empty(libraries.Libraries.Single(library => library.Id == _w.TvLibrary).Sources);
        // The folder is whole again, so it can be changed to another kind now.
        await _w.Service.PreviewAsync(folder.Id, new() { KindKey = "other_videos", LibraryId = _w.VideosLibrary }, default);
    }

    [Fact]
    public async Task A_job_resumed_after_a_stop_between_the_placement_and_the_new_titles_values_works_them_out_again()
    {
        var folder = _w.AddFolder(_w.MoviesLibrary, "Films", managed: false);
        var file = _w.AddFile(folder, "film.mkv", MediaType.Movies, "A film");
        var status = await _w.Service.StartAsync(folder.Id, new() { KindKey = "other_videos", LibraryId = _w.VideosLibrary }, default);
        var operationId = Guid.Parse(status.OperationId);
        // The first attempt placed the file and was stopped before the new title's values were worked out.
        var replaced = await _w.Changes.ReplaceUnderMediaTypeAsync(file.AssetId, MediaType.Videos, new Dictionary<string, string> { ["title"] = "A film" }, file.Path);
        _w.Execute("DELETE FROM canonical_values WHERE entity_id = @work;", new { work = replaced.WorkId });
        _w.Execute("INSERT INTO metadata_claims (id, entity_id, provider_id, claim_key, claim_value, confidence, claimed_at, is_user_locked) VALUES (@id, @work, @manual, 'title', 'My own title', 1, '2026-01-03', 1);",
            new { id = Guid.NewGuid(), work = replaced.WorkId, manual = WellKnownProviders.UserManual });
        await _w.Ledger.UpsertAsync(new FolderOrganiseLogEntry(
            operationId, Guid.Parse(folder.Id), file.Path, file.Path, FolderOrganiseOutcomes.Moving, DateTimeOffset.UtcNow.AddMinutes(-1)));

        await _w.Runner.RunAsync(operationId, Guid.Parse(folder.Id), new ScriptedHost(allowFiles: 10), default);

        Assert.Equal("My own title", _w.Scalar<string>("SELECT value FROM canonical_values WHERE entity_id = @work AND key = 'title';", new { work = replaced.WorkId }));
        Assert.Equal(_w.VideosLibrary, _w.Scalar<string>("SELECT library_id FROM media_assets WHERE id = @id;", new { id = file.AssetId }));
    }

    [Theory]
    [InlineData("place", false)]
    [InlineData("drop", false)]
    [InlineData("rescore", false)]
    [InlineData("library", false)]
    [InlineData("jobs", false)]
    [InlineData("place", true)]
    [InlineData("drop", true)]
    [InlineData("rescore", true)]
    [InlineData("library", true)]
    [InlineData("jobs", true)]
    public async Task A_run_stopped_after_any_step_is_finished_by_the_same_job_resuming_and_the_file_ends_fully_changed(string step, bool processStopped)
    {
        var folder = _w.AddFolder(_w.MoviesLibrary, "Films", managed: true);
        var file = _w.AddFile(folder, "film.mkv", MediaType.Movies, "A film");
        GivePersonalState(file);
        var status = await _w.Service.StartAsync(folder.Id, new() { KindKey = "other_videos", LibraryId = _w.VideosLibrary }, default);
        var operationId = Guid.Parse(status.OperationId);
        _w.Stop.Crash = processStopped;
        StopAfter(step);

        if (processStopped)
        {
            // The whole process stops between two writes: nothing is logged, so the file's ledger line is still "moving".
            await Assert.ThrowsAsync<OperationCanceledException>(
                () => _w.Runner.RunAsync(operationId, Guid.Parse(folder.Id), new ScriptedHost(allowFiles: 10), default));
            Assert.Equal(FolderOrganiseOutcomes.Moving, (await _w.Ledger.GetAsync(operationId, file.Path))!.Outcome);
        }
        else
        {
            await _w.RunWorkerUntilFinishedAsync(status.OperationId);
            Assert.Equal("failed", (await _w.Jobs.GetAsync(operationId, default))!.State);
        }

        var resumed = await _w.Runner.RunAsync(operationId, Guid.Parse(folder.Id), new ScriptedHost(allowFiles: 10), default);

        Assert.Equal((FolderOrganiseStop.Finished, 0), (resumed.Stop, resumed.Failed));
        AssertFullyChanged(file, _w.VideosLibrary, "Videos", plainTarget: true, managed: true);
        Assert.Equal("kind_changed", (await _w.Ledger.GetAsync(operationId, file.Path))!.Outcome);
        Assert.Equal(1, _w.Scalar<int>("SELECT COUNT(*) FROM media_file_write_intents;"));
    }

    [Theory]
    [InlineData("place")]
    [InlineData("drop")]
    [InlineData("rescore")]
    [InlineData("library")]
    [InlineData("jobs")]
    public async Task A_run_stopped_after_any_step_is_finished_by_changing_the_folder_back_whatever_library_the_file_is_in(string step)
    {
        var folder = _w.AddFolder(_w.MoviesLibrary, "Films", managed: true);
        var file = _w.AddFile(folder, "film.mkv", MediaType.Movies, "A film");
        GivePersonalState(file);
        var status = await _w.Service.StartAsync(folder.Id, new() { KindKey = "other_videos", LibraryId = _w.VideosLibrary }, default);
        StopAfter(step);
        await _w.RunWorkerUntilFinishedAsync(status.OperationId);

        var back = await _w.Service.StartAsync(folder.Id, new() { KindKey = "movies" }, default);
        await _w.RunWorkerUntilFinishedAsync(back.OperationId);

        Assert.Equal("completed", (await _w.Jobs.GetAsync(Guid.Parse(back.OperationId), default))!.State);
        AssertFullyChanged(file, _w.MoviesLibrary, "Movies", plainTarget: false, managed: true);
        Assert.Single(_w.Config.LoadLibraries().Libraries.Single(library => library.Id == _w.MoviesLibrary).Sources, source => source.Id == folder.Id);
    }

    [Fact]
    public async Task A_file_placed_under_a_TV_work_whose_rescore_then_stopped_ends_as_a_Movies_work_in_the_Movies_library_with_its_reaction_when_the_folder_is_changed_back()
    {
        var folder = _w.AddFolder(_w.MoviesLibrary, "Films", managed: false);
        var file = _w.AddFile(folder, "film.mkv", MediaType.Movies, "A film");
        _w.Execute("INSERT INTO profile_reactions (profile_id, entity_kind, entity_id, reaction, updated_at) VALUES (@person, 'Movie', @work, 'Love', '2026-10-01T00:00:00Z');",
            new { person = Profile.SeedProfileId, work = file.WorkId });
        _w.Rescoring.FailAfterRescore = 1;
        var forward = await _w.Service.StartAsync(folder.Id, new() { KindKey = "tv" }, default);
        await _w.RunWorkerUntilFinishedAsync(forward.OperationId);
        // The file was placed under a TV work, and the job stopped on it: it is still in the Movies library.
        Assert.Equal("TV", WorkTypeOf(file));
        Assert.Equal(_w.MoviesLibrary, _w.Scalar<string>("SELECT library_id FROM media_assets WHERE id = @id;", new { id = file.AssetId }));

        var back = await _w.Service.StartAsync(folder.Id, new() { KindKey = "movies" }, default);
        await _w.RunWorkerUntilFinishedAsync(back.OperationId);

        Assert.Equal("Movies", WorkTypeOf(file));
        Assert.Equal(_w.MoviesLibrary, _w.Scalar<string>("SELECT library_id FROM media_assets WHERE id = @id;", new { id = file.AssetId }));
        Assert.Equal(0, _w.Scalar<int>("SELECT COUNT(*) FROM works WHERE media_type = 'TV';"));
        var work = _w.Scalar<Guid>("SELECT e.work_id FROM media_assets ma JOIN editions e ON e.id = ma.edition_id WHERE ma.id = @id;", new { id = file.AssetId });
        Assert.Equal((work, "Love"), _w.Rows<(Guid, string)>("SELECT entity_id, reaction FROM profile_reactions WHERE profile_id = @person;", new { person = Profile.SeedProfileId }).Single());
    }

    [Fact]
    public async Task A_Movies_file_changed_to_TV_stopped_after_its_job_was_queued_and_changed_back_has_exactly_one_unfinished_job_of_type_Movies()
    {
        var folder = _w.AddFolder(_w.MoviesLibrary, "Films", managed: true);
        var file = _w.AddFile(folder, "film.mkv", MediaType.Movies, "A film");
        GivePersonalState(file);
        _w.Filing.FailAfterRequest = 1;
        var forward = await _w.Service.StartAsync(folder.Id, new() { KindKey = "tv" }, default);
        await _w.RunWorkerUntilFinishedAsync(forward.OperationId);
        // The job for TV was queued before the stop.
        Assert.Equal(["TV"], UnfinishedJobTypes(file));

        var back = await _w.Service.StartAsync(folder.Id, new() { KindKey = "movies" }, default);
        await _w.RunWorkerUntilFinishedAsync(back.OperationId);

        Assert.Equal("completed", (await _w.Jobs.GetAsync(Guid.Parse(back.OperationId), default))!.State);
        Assert.Equal(["Movies"], UnfinishedJobTypes(file));
        AssertFullyChanged(file, _w.MoviesLibrary, "Movies", plainTarget: false, managed: true);
    }

    [Fact]
    public async Task The_same_file_retried_after_its_identity_job_was_queued_still_has_only_one_identity_job()
    {
        var folder = _w.AddFolder(_w.VideosLibrary, "Films", managed: true);
        var file = _w.AddFile(folder, "film.mkv", MediaType.Videos, "A film");
        var status = await _w.Service.StartAsync(folder.Id, new() { KindKey = "movies" }, default);
        var operationId = Guid.Parse(status.OperationId);
        _w.Filing.FailAfterRequest = 1;

        await _w.RunWorkerUntilFinishedAsync(status.OperationId);
        await _w.Runner.RunAsync(operationId, Guid.Parse(folder.Id), new ScriptedHost(allowFiles: 10), default);

        Assert.Equal(1, _w.Scalar<int>("SELECT COUNT(*) FROM identity_jobs WHERE entity_id = @id;", new { id = file.AssetId }));
        Assert.Equal("kind_changed", (await _w.Ledger.GetAsync(operationId, file.Path))!.Outcome);
    }

    [Fact]
    public async Task A_folder_whose_new_library_cannot_be_saved_keeps_its_first_check_and_is_not_marked_applied()
    {
        var folder = _w.AddFolder(_w.MoviesLibrary, "Films", managed: true);
        _w.AddFile(folder, "film.mkv", MediaType.Movies, "A film");
        await _w.FirstChecks.StartAsync(_w.MoviesLibrary, folder.Id, folder.Path, DateTimeOffset.UtcNow);
        var status = await _w.Service.StartAsync(folder.Id, new() { KindKey = "other_videos", LibraryId = _w.VideosLibrary }, default);
        var operationId = Guid.Parse(status.OperationId);
        var runner = _w.CreateRunner(FolderJobServiceTests.FailingSaveLoader.Create(_w.Config, failFromSave: 1));

        await Assert.ThrowsAsync<IOException>(() => runner.RunAsync(operationId, Guid.Parse(folder.Id), new ScriptedHost(allowFiles: 10), default));

        Assert.Equal(("checking", _w.MoviesLibrary), ((await _w.FirstChecks.GetAsync(folder.Id))!.State, (await _w.FirstChecks.GetAsync(folder.Id))!.LibraryId));
        Assert.Null((await _w.Changes.GetPlanAsync(operationId))!.ConfigAppliedAt);
        Assert.Single(_w.Config.LoadLibraries().Libraries.Single(library => library.Id == _w.MoviesLibrary).Sources);
    }

    [Fact]
    public async Task A_file_left_in_moving_after_its_identity_job_was_queued_is_not_given_a_second_job_when_the_job_resumes()
    {
        var folder = _w.AddFolder(_w.VideosLibrary, "Films", managed: false);
        var file = _w.AddFile(folder, "film.mkv", MediaType.Videos, "A film");
        var status = await _w.Service.StartAsync(folder.Id, new() { KindKey = "movies" }, default);
        var operationId = Guid.Parse(status.OperationId);
        // An earlier attempt wrote its intent, queued the job and was stopped before it could finish the file.
        await _w.Ledger.UpsertAsync(new FolderOrganiseLogEntry(
            operationId, Guid.Parse(folder.Id), file.Path, file.Path, FolderOrganiseOutcomes.Moving, DateTimeOffset.UtcNow.AddMinutes(-1)));
        await new IdentityJobRepository(_w.Database).CreateAsync(new IdentityJob
        {
            EntityId = file.AssetId,
            EntityType = "MediaAsset",
            MediaType = "Movies",
            Pass = "Quick",
        });

        await _w.Runner.RunAsync(operationId, Guid.Parse(folder.Id), new ScriptedHost(allowFiles: 10), default);

        Assert.Equal(1, _w.Scalar<int>("SELECT COUNT(*) FROM identity_jobs WHERE entity_id = @id;", new { id = file.AssetId }));
        Assert.Equal(_w.MoviesLibrary, _w.Scalar<string>("SELECT library_id FROM media_assets WHERE id = @id;", new { id = file.AssetId }));
    }

    [Fact]
    public async Task The_activity_entry_names_the_administrator_who_started_the_change()
    {
        var folder = _w.AddFolder(_w.MoviesLibrary, "Films", managed: false);
        _w.AddFile(folder, "film.mkv", MediaType.Movies, "A film");
        var admin = Guid.NewGuid();

        var status = await _w.Service.StartAsync(folder.Id, new() { KindKey = "other_videos", LibraryId = _w.VideosLibrary }, default, admin);
        await _w.RunWorkerUntilFinishedAsync(status.OperationId);

        Assert.Equal(admin, Assert.Single(await _w.Activity.GetRecentAsync(20), entry => entry.ActionType == SystemActionType.FolderKindChanged).ProfileId);
    }

    [Fact]
    public async Task A_title_a_person_locked_shows_its_locked_value_on_the_new_work_after_the_file_is_placed_again()
    {
        var folder = _w.AddFolder(_w.MoviesLibrary, "Films", managed: false);
        var file = _w.AddFile(folder, "film.mkv", MediaType.Movies, "A film");
        _w.Execute("""
            INSERT INTO metadata_claims (id, entity_id, provider_id, claim_key, claim_value, confidence, claimed_at, is_user_locked)
            VALUES (@claim, @work, @manual, 'title', 'My own title', 1, '2026-01-03', 1);
            """, new { claim = Guid.NewGuid(), work = file.WorkId, manual = WellKnownProviders.UserManual });

        var status = await _w.Service.StartAsync(folder.Id, new() { KindKey = "other_videos", LibraryId = _w.VideosLibrary }, default);
        await _w.RunWorkerUntilFinishedAsync(status.OperationId);

        var newWork = _w.Scalar<Guid>("SELECT e.work_id FROM media_assets ma JOIN editions e ON e.id = ma.edition_id WHERE ma.id = @id;", new { id = file.AssetId });
        Assert.NotEqual(file.WorkId, newWork);
        Assert.Equal("My own title", _w.Scalar<string>("SELECT value FROM canonical_values WHERE entity_id = @id AND key = 'title';", new { id = newWork }));
    }

    // ----- refusals ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_change_is_refused_for_a_Vault_kind_a_Vault_folder_another_area_the_same_kind_and_unknown_things_and_nothing_is_started()
    {
        var folder = _w.AddFolder(_w.MoviesLibrary, "Films", managed: false);
        var vaultFolder = _w.AddFolder(_w.VaultLibrary, "Private", managed: false);
        var musicFolder = _w.AddFolder(_w.MusicLibrary, "Songs", managed: false);
        _w.AddFile(folder, "film.mkv", MediaType.Movies, "A film");

        async Task<FolderJobException> Refused(string source, FolderKindChangeRequest request)
        {
            var preview = await Assert.ThrowsAsync<FolderJobException>(() => _w.Service.PreviewAsync(source, request, default));
            var start = await Assert.ThrowsAsync<FolderJobException>(() => _w.Service.StartAsync(source, request, default));
            Assert.Equal(preview.Message, start.Message);
            return start;
        }

        Assert.Contains("Vault", (await Refused(folder.Id, new() { KindKey = "adult_content" })).Message, StringComparison.Ordinal);
        Assert.Contains("not in the same area", (await Refused(folder.Id, new() { KindKey = "music" })).Message, StringComparison.Ordinal);
        Assert.Contains("already is Movies", (await Refused(folder.Id, new() { KindKey = "movies" })).Message, StringComparison.Ordinal);
        Assert.Contains("does not exist", (await Refused(folder.Id, new() { KindKey = "no_such_kind" })).Message, StringComparison.Ordinal);
        Assert.Contains("Choose what", (await Refused(folder.Id, new() { KindKey = " " })).Message, StringComparison.Ordinal);
        // A kind with no library yet can be previewed (as a new library) but not started: the library is created first.
        Assert.Contains("Create it first", (await Assert.ThrowsAsync<FolderJobException>(() =>
            _w.Service.StartAsync(musicFolder.Id, new() { KindKey = "audiobooks" }, default))).Message, StringComparison.Ordinal);
        var vault = await Refused(vaultFolder.Id, new() { KindKey = "other_videos" });
        Assert.True(vault.NotFound);
        Assert.Equal("This folder is not part of your libraries.", vault.Message);
        Assert.True((await Refused(Guid.NewGuid().ToString("D"), new() { KindKey = "other_videos" })).NotFound);
        Assert.Contains("Choose a folder", (await Refused("not-a-guid", new() { KindKey = "other_videos" })).Message, StringComparison.Ordinal);

        Assert.Empty(await _w.Operations.GetQueueAsync(MediaOperationQueue.FolderJobs, 50));
        Assert.Equal(_w.MoviesLibrary, _w.Config.LoadLibraries().Libraries.Single(library => library.Sources.Any(source => source.Id == folder.Id)).Id);
    }

    [Fact]
    public async Task A_library_that_is_not_of_the_chosen_kind_or_does_not_exist_is_refused()
    {
        var folder = _w.AddFolder(_w.MoviesLibrary, "Films", managed: false);

        var wrongKind = await Assert.ThrowsAsync<FolderJobException>(() =>
            _w.Service.StartAsync(folder.Id, new() { KindKey = "other_videos", LibraryId = _w.YouTubeLibrary }, default));
        var unknown = await Assert.ThrowsAsync<FolderJobException>(() =>
            _w.Service.StartAsync(folder.Id, new() { KindKey = "other_videos", LibraryId = Guid.NewGuid().ToString("D") }, default));

        // A Vault library answers like a library that does not exist.
        var vault = await Assert.ThrowsAsync<FolderJobException>(() =>
            _w.Service.StartAsync(folder.Id, new() { KindKey = "other_videos", LibraryId = _w.VaultLibrary }, default));

        Assert.Equal("That library is not a Videos library.", wrongKind.Message);
        Assert.Equal("That library does not exist.", unknown.Message);
        Assert.Equal("That library does not exist.", vault.Message);
        Assert.Empty(await _w.Operations.GetQueueAsync(MediaOperationQueue.FolderJobs, 50));
    }

    // ----- named libraries ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_kind_with_several_named_libraries_moves_the_folder_into_the_one_named_by_id()
    {
        var lectures = _w.AddLibrary("Lectures", "other_videos", LibraryAreas.Watch, "Videos");
        var folder = _w.AddFolder(_w.MoviesLibrary, "Talks", managed: false);
        var file = _w.AddFile(folder, "talk.mkv", MediaType.Movies, "A talk");

        var ambiguous = await Assert.ThrowsAsync<FolderJobException>(() => _w.Service.StartAsync(folder.Id, new() { KindKey = "other_videos" }, default));
        var status = await _w.Service.StartAsync(folder.Id, new() { KindKey = "other_videos", LibraryId = lectures }, default);
        await _w.RunWorkerUntilFinishedAsync(status.OperationId);

        Assert.Contains("Choose which Videos library", ambiguous.Message, StringComparison.Ordinal);
        Assert.Equal(lectures, _w.Scalar<string>("SELECT library_id FROM media_assets WHERE id = @id;", new { id = file.AssetId }));
        Assert.Single(_w.Config.LoadLibraries().Libraries.Single(library => library.Id == lectures).Sources);
        Assert.Empty(_w.Config.LoadLibraries().Libraries.Single(library => library.Id == _w.VideosLibrary).Sources);
    }

    // ----- the preview -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_preview_returns_the_counts_the_flags_and_Sam_when_Sam_hides_the_target_library()
    {
        var folder = _w.AddFolder(_w.MoviesLibrary, "Films", managed: true);
        for (var index = 0; index < 3; index++)
        {
            _w.AddFile(folder, $"film{index}.mkv", MediaType.Movies, $"Film {index}");
        }

        var sam = await _w.CreateAccountAsync("sam@example.test", "Sam", hidden: [Guid.Parse(_w.VideosLibrary)]);

        var preview = await _w.Service.PreviewAsync(folder.Id, new() { KindKey = "other_videos", LibraryId = _w.VideosLibrary }, default);

        Assert.Equal(3, preview.FileCount);
        Assert.Equal(3, preview.FilesToRefile);
        Assert.True(preview.DropsDetails);
        Assert.False(preview.StartsFirstCheck);
        Assert.True(preview.OtherAppsWarning);
        Assert.Equal(("Movies", "Videos", _w.VideosLibrary), (preview.FromKindLabel, preview.ToKindLabel, preview.ToLibraryId));
        Assert.True(preview.VisibilityLossChecked);
        var loss = Assert.Single(preview.VisibilityLoss);
        Assert.Equal((sam.Id, "Sam", 3), (loss.AccountId, loss.DisplayName, loss.FileCount));
        // Nothing was changed by asking.
        Assert.Empty(await _w.Operations.GetQueueAsync(MediaOperationQueue.FolderJobs, 50));
        Assert.Equal(_w.MoviesLibrary, _w.Scalar<string>("SELECT library_id FROM media_assets LIMIT 1;"));
    }

    [Fact]
    public async Task The_preview_of_a_Read_only_folder_moves_nothing_and_a_lookup_target_says_it_starts_a_first_check()
    {
        var folder = _w.AddFolder(_w.VideosLibrary, "Clips", managed: false);
        _w.AddFile(folder, "one.mkv", MediaType.Videos, "One");

        var preview = await _w.Service.PreviewAsync(folder.Id, new() { KindKey = "tv" }, default);

        Assert.Equal((1, 0, false, true, false), (preview.FileCount, preview.FilesToRefile, preview.DropsDetails, preview.StartsFirstCheck, preview.OtherAppsWarning));
    }

    [Fact]
    public async Task A_preview_with_no_library_is_about_a_new_library_of_the_kind_and_creates_nothing_and_nobody_loses_sight()
    {
        var folder = _w.AddFolder(_w.MusicLibrary, "Songs", managed: true);
        _w.AddFile(folder, "one.mp3", MediaType.Music, "One");
        _w.AddFile(folder, "two.mp3", MediaType.Music, "Two");
        var librariesBefore = _w.Config.LoadLibraries().Libraries.Count;
        // Nobody could lose sight of anything, so the visibility question is not even asked.
        _w.Visibility.Failure = new IOException("The visibility check must not run for a library that does not exist.");

        // Audiobooks has no library yet; Other audio holds several named libraries, so no existing one can be meant.
        var audiobooks = await _w.Service.PreviewAsync(folder.Id, new() { KindKey = "audiobooks" }, default);
        var named = await _w.Service.PreviewAsync(folder.Id, new() { KindKey = "other_audio" }, default);

        Assert.Equal((2, 2, false, true, true), (audiobooks.FileCount, audiobooks.FilesToRefile, audiobooks.DropsDetails, audiobooks.StartsFirstCheck, audiobooks.OtherAppsWarning));
        Assert.Equal((2, 2, true, false), (named.FileCount, named.FilesToRefile, named.DropsDetails, named.StartsFirstCheck));
        Assert.All([audiobooks, named], preview =>
        {
            Assert.True(preview.VisibilityLossChecked);
            Assert.Empty(preview.VisibilityLoss);
            Assert.Equal(string.Empty, preview.ToLibraryId);
        });
        Assert.Equal(librariesBefore, _w.Config.LoadLibraries().Libraries.Count);
        Assert.Empty(await _w.Operations.GetQueueAsync(MediaOperationQueue.FolderJobs, 50));

        // Starting still needs a real library.
        var start = await Assert.ThrowsAsync<FolderJobException>(() => _w.Service.StartAsync(folder.Id, new() { KindKey = "audiobooks" }, default));
        Assert.Contains("Create it first", start.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_blank_library_means_the_one_existing_library_of_a_single_library_kind_in_both_the_preview_and_the_start()
    {
        var folder = _w.AddFolder(_w.VideosLibrary, "Films", managed: false);
        var file = _w.AddFile(folder, "film.mkv", MediaType.Videos, "A film");

        var preview = await _w.Service.PreviewAsync(folder.Id, new() { KindKey = "movies" }, default);
        var status = await _w.Service.StartAsync(folder.Id, new() { KindKey = "movies" }, default);
        await _w.RunWorkerUntilFinishedAsync(status.OperationId);

        Assert.Equal(_w.MoviesLibrary, preview.ToLibraryId);
        Assert.Equal(_w.MoviesLibrary, (await _w.Changes.GetPlanAsync(Guid.Parse(status.OperationId)))!.ToLibraryId);
        Assert.Equal(_w.MoviesLibrary, _w.Scalar<string>("SELECT library_id FROM media_assets WHERE id = @id;", new { id = file.AssetId }));
    }

    [Fact]
    public async Task A_blank_library_for_a_kind_that_holds_several_libraries_is_a_new_library_in_the_preview_and_refused_by_the_start_even_with_only_one_library_existing()
    {
        var folder = _w.AddFolder(_w.MoviesLibrary, "Films", managed: false);
        var file = _w.AddFile(folder, "film.mkv", MediaType.Movies, "A film");

        var preview = await _w.Service.PreviewAsync(folder.Id, new() { KindKey = "other_videos" }, default);
        var start = await Assert.ThrowsAsync<FolderJobException>(() => _w.Service.StartAsync(folder.Id, new() { KindKey = "other_videos" }, default));

        // The one Videos library that exists is not quietly picked.
        Assert.Equal(string.Empty, preview.ToLibraryId);
        Assert.Contains("Choose which Videos library", start.Message, StringComparison.Ordinal);
        Assert.Empty(await _w.Operations.GetQueueAsync(MediaOperationQueue.FolderJobs, 50));
        Assert.Equal(_w.MoviesLibrary, _w.Scalar<string>("SELECT library_id FROM media_assets WHERE id = @id;", new { id = file.AssetId }));
    }

    [Fact]
    public async Task A_blank_library_is_refused_on_start_for_a_kind_with_several_libraries_and_never_quietly_picks_the_one_that_exists()
    {
        var folder = _w.AddFolder(_w.MusicLibrary, "Songs", managed: false);
        _w.AddFile(folder, "one.mp3", MediaType.Music, "One");
        // Other audio holds more than one named library, so a blank id cannot mean one of them; Audiobooks has none yet.
        var several = await Assert.ThrowsAsync<FolderJobException>(() => _w.Service.StartAsync(folder.Id, new() { KindKey = "other_audio" }, default));
        var none = await Assert.ThrowsAsync<FolderJobException>(() => _w.Service.StartAsync(folder.Id, new() { KindKey = "audiobooks" }, default));

        Assert.Contains("Choose which", several.Message, StringComparison.Ordinal);
        Assert.Contains("Create it first", none.Message, StringComparison.Ordinal);
        Assert.Empty(await _w.Operations.GetQueueAsync(MediaOperationQueue.FolderJobs, 50));
        Assert.Equal(_w.MusicLibrary, _w.Scalar<string>("SELECT library_id FROM media_assets LIMIT 1;"));
    }

    [Fact]
    public async Task A_visibility_answer_that_is_refused_or_fails_is_passed_on_as_not_checked_never_as_an_empty_list()
    {
        var folder = _w.AddFolder(_w.MoviesLibrary, "Films", managed: false);
        _w.AddFile(folder, "film.mkv", MediaType.Movies, "A film");

        _w.Visibility.Next = VisibilityAfterMoveResult.Refused("Files can't be moved into the Vault from here.");
        var refused = await _w.Service.PreviewAsync(folder.Id, new() { KindKey = "other_videos", LibraryId = _w.VideosLibrary }, default);
        _w.Visibility.Next = null;
        _w.Visibility.Failure = new IOException("The data store is locked.");
        var failed = await _w.Service.PreviewAsync(folder.Id, new() { KindKey = "other_videos", LibraryId = _w.VideosLibrary }, default);

        Assert.False(refused.VisibilityLossChecked);
        Assert.Equal("Files can't be moved into the Vault from here.", refused.VisibilityMessage);
        Assert.Empty(refused.VisibilityLoss);
        Assert.False(failed.VisibilityLossChecked);
        Assert.False(string.IsNullOrWhiteSpace(failed.VisibilityMessage));
    }

    // ----- who may ask -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_non_administrator_is_refused_on_both_actions_and_an_administrator_is_answered()
    {
        var folder = _w.AddFolder(_w.MoviesLibrary, "Films", managed: false);
        _w.AddFile(folder, "film.mkv", MediaType.Movies, "A film");
        await using var app = await StartAppAsync();
        var body = $$"""{"kind_key":"other_videos","library_id":"{{_w.VideosLibrary}}"}""";

        using var member = app.CreateClient("member");
        using var admin = app.CreateClient("admin");
        using var memberPreview = await member.PostAsync($"/settings/libraries/folders/{folder.Id}/change-kind/preview", Json(body));
        using var memberStart = await member.PostAsync($"/settings/libraries/folders/{folder.Id}/change-kind", Json(body));
        Assert.Equal(HttpStatusCode.Forbidden, memberPreview.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, memberStart.StatusCode);
        Assert.Empty(await _w.Operations.GetQueueAsync(MediaOperationQueue.FolderJobs, 50));

        using var preview = await admin.PostAsync($"/settings/libraries/folders/{folder.Id}/change-kind/preview", Json(body));
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal(1, JsonSerializer.Deserialize<FolderKindChangePreviewResponse>(await preview.Content.ReadAsStringAsync())!.FileCount);

        using var refused = await admin.PostAsync($"/settings/libraries/folders/{folder.Id}/change-kind", Json("""{"kind_key":"movies"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("already is Movies", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var started = await admin.PostAsync($"/settings/libraries/folders/{folder.Id}/change-kind", Json(body));
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        var job = JsonSerializer.Deserialize<FolderJobStatusDto>(await started.Content.ReadAsStringAsync())!;
        Assert.Equal("change_folder_kind", job.JobType);

        using var conflict = await admin.PostAsync($"/settings/libraries/folders/{folder.Id}/change-kind", Json(body));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    // ----- helpers -----------------------------------------------------------------------------------------------------------------

    // Makes the next run stop right after one step: the placement, the drop of the looked-up details, the rescore, the library, or the jobs.
    private void StopAfter(string step)
    {
        switch (step)
        {
            case "place": _w.FlakyChanges.FailAfterReplace = 1; break;
            case "drop": _w.FlakyChanges.FailAfterDrop = 1; break;
            case "rescore": _w.Rescoring.FailAfterRescore = 1; break;
            case "library": _w.LibraryHook.FailAfterSetLibrary = 1; break;
            case "jobs": _w.Filing.FailAfterRequest = 1; break;
            default: throw new ArgumentOutOfRangeException(nameof(step));
        }
    }

    private List<string> UnfinishedJobTypes(FileRef file) => _w.Rows<string>(
        "SELECT media_type FROM identity_jobs WHERE entity_id = @id AND state NOT IN ('Failed', 'Ready', 'ReadyWithoutUniverse', 'RetailNoMatch', 'QidNoMatch', 'QidNeedsReview');",
        new { id = file.AssetId });

    private string WorkTypeOf(FileRef file) => _w.Scalar<string>(
        "SELECT w.media_type FROM media_assets ma JOIN editions e ON e.id = ma.edition_id JOIN works w ON w.id = e.work_id WHERE ma.id = @id;", new { id = file.AssetId });

    // The file's own watch progress and reaction, which no kind change may lose.
    private void GivePersonalState(FileRef file) => _w.Execute("""
        INSERT INTO user_states (user_id, asset_id, progress_pct, last_accessed) VALUES (@person, @asset, 40, '2026-10-01T00:00:00Z');
        INSERT INTO profile_reactions (profile_id, entity_kind, entity_id, reaction, updated_at) VALUES (@person, 'Movie', @work, 'Love', '2026-10-01T00:00:00Z');
        """, new { person = Profile.SeedProfileId, asset = file.AssetId, work = file.WorkId });

    // The file is wholly what the kind says: in its library, under one work of the kind's media type (no leftover work of another type), with
    // its own title shown and its own details kept. A plain kind has none of the looked-up details and no identity job; a looking-up kind has
    // exactly one unfinished identity job under its media type. A Managed folder has its filing request, and the person's progress and reaction
    // are intact on the file's current work.
    private void AssertFullyChanged(FileRef file, string libraryId, string mediaType, bool plainTarget, bool managed)
    {
        Assert.Equal(libraryId, _w.Scalar<string>("SELECT library_id FROM media_assets WHERE id = @id;", new { id = file.AssetId }));
        Assert.Equal(mediaType, WorkTypeOf(file));
        Assert.Equal(mediaType, _w.Scalar<string>("SELECT value FROM canonical_values WHERE entity_id = @id AND key = 'media_type';", new { id = file.AssetId }));
        Assert.Equal(1, _w.Scalar<int>("SELECT COUNT(DISTINCT w.id) FROM works w WHERE w.media_type = @mediaType;", new { mediaType }));
        Assert.Equal(0, _w.Scalar<int>("SELECT COUNT(*) FROM works WHERE media_type <> @mediaType;", new { mediaType }));
        var work = _w.Scalar<Guid>("SELECT e.work_id FROM media_assets ma JOIN editions e ON e.id = ma.edition_id WHERE ma.id = @id;", new { id = file.AssetId });

        // The file's own details are never touched; the looked-up ones are gone for a plain kind.
        Assert.Equal(1, _w.Scalar<int>(
            "SELECT COUNT(*) FROM metadata_claims WHERE entity_id = @id AND provider_id = @local AND claim_value = @title;",
            new { id = file.AssetId, local = WellKnownProviders.LocalProcessor, title = file.Title }));
        if (plainTarget)
        {
            Assert.Equal(0, _w.Scalar<int>(
                "SELECT COUNT(*) FROM metadata_claims WHERE entity_id IN (@asset, @work, @file) AND provider_id = @tmdb;",
                new { asset = file.AssetId, work, file = file.WorkId, tmdb = WellKnownProviders.Tmdb }));
            Assert.Equal(file.Title, _w.Scalar<string>("SELECT value FROM canonical_values WHERE entity_id = @id AND key = 'title';", new { id = file.AssetId }));
            Assert.Empty(UnfinishedJobTypes(file));
        }
        else
        {
            Assert.Equal([mediaType], UnfinishedJobTypes(file));
        }

        Assert.Equal(managed ? 1 : 0, _w.Scalar<int>("SELECT COUNT(*) FROM media_file_write_intents WHERE asset_id = @id;", new { id = file.AssetId }));

        // Personal state: progress stays with the file and the reaction followed it to its current work.
        Assert.Equal(40, _w.Scalar<int>("SELECT progress_pct FROM user_states WHERE asset_id = @id;", new { id = file.AssetId }));
        Assert.Equal((work, "Love"), _w.Rows<(Guid, string)>("SELECT entity_id, reaction FROM profile_reactions WHERE profile_id = @person;", new { person = Profile.SeedProfileId }).Single());
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, MediaTypeHeaderValue.Parse("application/json"));

    private async Task<TestApp> StartAppAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization(options =>
            options.AddPolicy(AuthPolicies.Administrator, policy =>
                policy.RequireAuthenticatedUser().AddRequirements(new EffectiveAdministratorRequirement(true))));
        builder.Services.AddScoped<IAuthorizationHandler, EffectiveAdministratorHandler>();
        builder.Services.AddSingleton<IRequestAuthorityResolver, PrincipalHeaderResolver>();
        builder.Services.AddSingleton<IAccountAccessDecisionService>(new AdministratorDecisions());
        builder.Services.AddSingleton(_w.Service);
        builder.Services.AddSingleton(_w.Jobs);

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapFolderJobEndpoints();
        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        return new TestApp(app, new Uri(addresses!.Addresses.Single()));
    }

    private const string PrincipalHeader = "X-Test-Principal";
    private static readonly Guid AccountId = Guid.NewGuid();

    private static readonly IReadOnlyDictionary<string, RequestAuthority> Principals = new Dictionary<string, RequestAuthority>
    {
        ["admin"] = new(PrincipalKind.Human, true, AccountId, Guid.NewGuid(), null, Guid.NewGuid(), null,
            AccountEnabled: true, GrantEnabled: true, AccountIsAdministrator: true, GrantAdminEnabled: true),
        ["member"] = new(PrincipalKind.Human, true, AccountId, Guid.NewGuid(), null, Guid.NewGuid(), null,
            AccountEnabled: true, GrantEnabled: true),
    };

    private sealed class PrincipalHeaderResolver : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default) =>
            ValueTask.FromResult(Principals[context.Request.Headers[PrincipalHeader].ToString()]);
    }

    private sealed class TestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "folder-kind-test")], Scheme.Name)), Scheme.Name)));
    }

    private sealed class TestApp(WebApplication app, Uri address) : IAsyncDisposable
    {
        public HttpClient CreateClient(string principal)
        {
            var client = new HttpClient { BaseAddress = address };
            client.DefaultRequestHeaders.Add(PrincipalHeader, principal);
            return client;
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    /// <summary>Lets a run do a number of files, then asks it to pause (a pause, or the Engine stopping).</summary>
    private sealed class ScriptedHost(int allowFiles) : IFolderOrganiseHost
    {
        private int _asked;

        public Task<FolderJobSignal> GetSignalAsync(CancellationToken ct) =>
            Task.FromResult(++_asked <= allowFiles ? FolderJobSignal.Continue : FolderJobSignal.Pause);

        public Task ReportProgressAsync(FolderOrganiseProgress progress, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed record FileRef(Guid AssetId, Guid WorkId, string Path, string Title);

    /// <summary>The filing queue of the Engine, recording what the job asked of it.</summary>
    private sealed class RecordingFilingQueue(IFilingQueue inner, StopSwitch stop) : IFilingQueue
    {
        public List<(Guid AssetId, bool Held)> Requests { get; } = [];

        /// <summary>How many times to throw right after a filing request was recorded (a stop after the jobs were written).</summary>
        public int FailAfterRequest { get; set; }

        public async Task<bool> RequestFilingAsync(Guid assetId, bool held, CancellationToken ct = default)
        {
            Requests.Add((assetId, held));
            var recorded = await inner.RequestFilingAsync(assetId, held, ct);
            return FailAfterRequest-- > 0 ? throw stop.Make("Stopped after the filing request.") : recorded;
        }
    }

    private sealed class RecordingRereader : IAssetRereader
    {
        public List<Guid> Assets { get; } = [];

        public Guid? FailFor { get; set; }

        public Task<FileMetadataRereadResult> RereadAsync(Guid assetId, CancellationToken ct)
        {
            if (assetId == FailFor)
            {
                throw new IOException("The file is locked by another program.");
            }

            Assets.Add(assetId);
            return Task.FromResult(new FileMetadataRereadResult(assetId, "refreshed", true, false, string.Empty));
        }
    }

    private sealed class SwitchableVisibility(IVisibilityAfterMoveReadService inner) : IVisibilityAfterMoveReadService
    {
        public VisibilityAfterMoveResult? Next { get; set; }

        public Exception? Failure { get; set; }

        public Task<VisibilityAfterMoveResult> GetAsync(VisibilityAfterMoveRequest request, CancellationToken ct = default) =>
            Failure is not null ? throw Failure : Next is not null ? Task.FromResult(Next) : inner.GetAsync(request, ct);
    }

    /// <summary>The data side of the change with a re-place that can be made to throw (once, or for one file), as a locked file or a full disk would.</summary>
    /// <summary>
    /// How a stop looks: an error the job logs (the file is marked failed), or a stop of the whole process (nothing is logged and the file's ledger
    /// line stays <c>moving</c>, as when the Engine is killed between two writes).
    /// </summary>
    public sealed class StopSwitch
    {
        public bool Crash { get; set; }

        public Exception Make(string message) => Crash ? new OperationCanceledException(message) : new IOException(message);
    }

    private sealed class FlakyChangeRepository(IFolderKindChangeRepository inner, StopSwitch stop) : IFolderKindChangeRepository
    {
        public int ReplaceFailures { get; set; }

        public int ReplaceCalls { get; private set; }

        /// <summary>How many times to throw right after the placement was made (a stop between the placement and what follows).</summary>
        public int FailAfterReplace { get; set; }

        /// <summary>How many times to throw right after the looked-up details were dropped.</summary>
        public int FailAfterDrop { get; set; }

        public Task<IReadOnlyList<Guid>> ListPlanOperationIdsAsync(Guid sourceId, CancellationToken ct = default) => inner.ListPlanOperationIdsAsync(sourceId, ct);

        public Task TidyEmptyPlacesAsync(CancellationToken ct = default) => inner.TidyEmptyPlacesAsync(ct);

        public Task SavePlanAsync(FolderKindChangePlan plan, CancellationToken ct = default) => inner.SavePlanAsync(plan, ct);

        public Task<FolderKindChangePlan?> GetPlanAsync(Guid operationId, CancellationToken ct = default) => inner.GetPlanAsync(operationId, ct);

        public Task<FolderKindChangePlan?> GetLatestPlanAsync(Guid sourceId, CancellationToken ct = default) => inner.GetLatestPlanAsync(sourceId, ct);

        public Task<bool> MarkConfigAppliedAsync(Guid operationId, DateTimeOffset at, CancellationToken ct = default) => inner.MarkConfigAppliedAsync(operationId, at, ct);

        public Task<AssetWorkPlacement?> GetPlacementAsync(Guid assetId, CancellationToken ct = default) => inner.GetPlacementAsync(assetId, ct);

        public async Task<AssetReplacement> ReplaceUnderMediaTypeAsync(
            Guid assetId, MediaType mediaType, IReadOnlyDictionary<string, string> metadata, string? sourceFilePath, CancellationToken ct = default)
        {
            if (++ReplaceCalls > 0 && ReplaceFailures-- > 0)
            {
                throw new IOException("The data store is busy.");
            }

            var replaced = await inner.ReplaceUnderMediaTypeAsync(assetId, mediaType, metadata, sourceFilePath, ct);
            return FailAfterReplace-- > 0 && replaced.Replaced ? throw stop.Make("Stopped after the placement.") : replaced;
        }

        public async Task<DroppedProviderDetails> DropProviderDetailsAsync(Guid assetId, CancellationToken ct = default)
        {
            var dropped = await inner.DropProviderDetailsAsync(assetId, ct);
            return FailAfterDrop-- > 0 ? throw stop.Make("Stopped after the details were dropped.") : dropped;
        }
    }

    /// <summary>The rescoring step, which can be made to stop right after it ran.</summary>
    private sealed class FlakyRescorer(IEntityRescorer inner, StopSwitch stop) : IEntityRescorer
    {
        public int FailAfterRescore { get; set; }

        public async Task RescoreAsync(Guid entityId, CancellationToken ct)
        {
            await inner.RescoreAsync(entityId, ct);
            if (FailAfterRescore-- > 0)
            {
                throw stop.Make("Stopped after the values were worked out.");
            }
        }
    }

    /// <summary>The asset records, where setting a file's library can be made to stop right after it was written.</summary>
    public class FlakyAssetsProxy : DispatchProxy
    {
        private IMediaAssetRepository _inner = null!;
        private LibraryHook _hook = null!;

        public static IMediaAssetRepository Create(IMediaAssetRepository inner, LibraryHook hook)
        {
            var proxy = Create<IMediaAssetRepository, FlakyAssetsProxy>();
            var assets = (FlakyAssetsProxy)(object)proxy;
            assets._inner = inner;
            assets._hook = hook;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            try
            {
                var result = targetMethod!.Invoke(_inner, args);
                return targetMethod.Name == nameof(IMediaAssetRepository.SetLibraryIdAsync) && _hook.FailAfterSetLibrary-- > 0
                    ? ThrowAfter((Task)result!, _hook)
                    : result;
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                throw ex.InnerException;
            }
        }

        private static async Task ThrowAfter(Task written, LibraryHook hook)
        {
            await written;
            throw hook.Make("Stopped after the library was set.");
        }
    }

    public sealed class LibraryHook(StopSwitch stop)
    {
        public int FailAfterSetLibrary { get; set; }

        public Exception Make(string message) => stop.Make(message);
    }

    private sealed class FixedKinds(IReadOnlyList<LibraryKindDefinition> kinds) : ILibraryKindSource
    {
        public IReadOnlyList<LibraryKindDefinition> GetKinds() => kinds;

        public event Action? Changed { add { } remove { } }
    }

    private sealed class NullPublisher : IEventPublisher
    {
        public Task PublishAsync<TPayload>(string eventName, TPayload payload, CancellationToken ct = default)
            where TPayload : notnull => Task.CompletedTask;
    }

    // ── The world ────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class World : IDisposable
    {
        private static readonly DateTimeOffset Now = new(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);

        private readonly AccountRepository _accounts;
        private readonly IMediaAssetRepository _assets;

        public World()
        {
            LibraryHook = new LibraryHook(Stop);
            Root = Path.Combine(Path.GetTempPath(), "tuvima-kind-change", Guid.NewGuid().ToString("N"));
            MediaRoot = Path.Combine(Root, "media");
            Directory.CreateDirectory(MediaRoot);
            DapperConfiguration.Configure();
            Database = new DatabaseConnection(Path.Combine(Root, "tuvima.db"));
            Database.InitializeSchema();
            Database.RunStartupChecks();

            Config = new ConfigurationDirectoryLoader(Path.Combine(Root, "config"));
            Config.SaveLibraries(new LibrariesConfiguration
            {
                StorageLocations =
                [
                    new ServerStorageLocationConfig { Id = "media", Label = "Media", Path = MediaRoot, AllowWrite = true },
                    new ServerStorageLocationConfig { Id = "view", Label = "View", Path = Path.Combine(Root, "view"), AllowWrite = true },
                ],
                ViewStorage = new ViewStorageConfig { StorageLocationId = "view", RelativeRoot = "." },
                Libraries = [],
            });
            Directory.CreateDirectory(Path.Combine(Root, "view"));
            MoviesLibrary = AddLibrary("Movies", "movies", LibraryAreas.Watch, "Movies");
            VideosLibrary = AddLibrary("Videos", "other_videos", LibraryAreas.Watch, "Videos", plain: true);
            YouTubeLibrary = AddLibrary("YouTube", "youtube", LibraryAreas.Watch, "Videos", youtube: true);
            TvLibrary = AddLibrary("TV Shows", "tv", LibraryAreas.Watch, "TV");
            MusicLibrary = AddLibrary("Music", "music", LibraryAreas.Listen, "Music");
            AudioLibrary = AddLibrary("Other audio", "other_audio", LibraryAreas.Listen, "Music", plain: true);
            VaultLibrary = AddLibrary("Private", "other_videos", LibraryAreas.Watch, "Videos", plain: true);

            Operations = new MediaOperationRepository(Database);
            Ledger = new FolderOrganiseLogRepository(Database);
            Activity = new SystemActivityRepository(Database);
            _assets = new MediaAssetRepository(Database);
            _accounts = new AccountRepository(Database);
            FirstChecks = new FolderFirstCheckRepository(Database);
            var vaultLibraries = new VaultLibraryRepository(Database);
            using (var connection = Database.CreateConnection())
            {
                connection.Execute(
                    "INSERT INTO vault_libraries(library_id, lookup_source, adults_confirmed_at, created_at, updated_at) VALUES(@vault,'none',@now,@now,@now);",
                    new { vault = VaultLibrary, now = Now.ToString("O") });
            }

            var tracker = new MediaOperationTracker(
                Operations, new MediaOperationEventRepository(Database), new NullPublisher(), NullLogger<MediaOperationTracker>.Instance, vaultLibraries);
            Jobs = new FolderJobService(
                Config, Operations, tracker, Ledger, null!, null!,
                new ServerFolderBrowserService(Config, NullLogger<ServerFolderBrowserService>.Instance),
                NullLogger<FolderJobService>.Instance, vaultLibraries);

            var vaultKind = new LibraryKindDefinition
            {
                Key = "adult_content",
                Area = LibraryAreas.Vault,
                Label = "Adult content",
                Format = "Videos",
                Lookup = LibraryKindLookup.None,
                Grouping = LibraryKindGroupings.FolderAndTags,
                NamingTemplate = "default",
            };
            var kinds = new FixedKinds([.. LibraryKindsConfiguration.BuiltIn(), vaultKind]);
            var visibilityRule = new LibraryVisibilityRule(_accounts, vaultLibraries);
            Visibility = new SwitchableVisibility(new VisibilityAfterMoveReadService(
                _accounts, new ProfileRepository(Database), new MediaAssetLibraryCountRepository(Database), vaultLibraries, Config, visibilityRule));

            var resolver = new HierarchyResolver(new WorkRepository(Database));
            Changes = new FolderKindChangeRepository(Database, resolver);
            Service = new FolderKindChangeService(Config, kinds, vaultLibraries, _assets, Operations, Ledger, Visibility, Jobs, Changes, tracker, NullLogger<FolderKindChangeService>.Instance);

            Filing = new RecordingFilingQueue(new MediaFileWriteIntentRepository(Database), Stop);
            Rereader = new RecordingRereader();
            _kinds = kinds;
            _firstCheckService = new FolderFirstCheckService(FirstChecks, Config, NullLogger<FolderFirstCheckService>.Instance);
            FlakyChanges = new FlakyChangeRepository(Changes, Stop);
            Runner = CreateRunner(Config);
            Worker = new FolderOrganiseJobWorker(Operations, tracker, null!, null!, Jobs, NullLogger<FolderOrganiseJobWorker>.Instance, Runner);
        }

        private readonly FixedKinds _kinds;
        private readonly FolderFirstCheckService _firstCheckService;

        public string Root { get; }
        public string MediaRoot { get; }
        public FlakyChangeRepository FlakyChanges { get; }
        public FlakyRescorer Rescoring { get; private set; } = null!;
        public StopSwitch Stop { get; } = new();
        public LibraryHook LibraryHook { get; }
        public DatabaseConnection Database { get; }
        public ConfigurationDirectoryLoader Config { get; }
        public IMediaOperationRepository Operations { get; }
        public IFolderOrganiseLogRepository Ledger { get; }
        public SystemActivityRepository Activity { get; }
        public FolderFirstCheckRepository FirstChecks { get; }
        public FolderJobService Jobs { get; }
        public FolderKindChangeService Service { get; }
        public FolderKindChangeRepository Changes { get; }
        public FolderKindChangeRunner Runner { get; }
        public RecordingFilingQueue Filing { get; }
        public RecordingRereader Rereader { get; }
        public SwitchableVisibility Visibility { get; }
        public string MoviesLibrary { get; }
        public string VideosLibrary { get; }
        public string YouTubeLibrary { get; }
        public string TvLibrary { get; }
        public string MusicLibrary { get; }
        public string AudioLibrary { get; }
        public string VaultLibrary { get; }
        private FolderOrganiseJobWorker Worker { get; }

        /// <summary>The job's runner over the given libraries file (a failing one, say), the real data store, and a data side that can be made to fail.</summary>
        public FolderKindChangeRunner CreateRunner(IConfigurationLoader configuration)
        {
            var canonicals = new CanonicalValueRepository(Database);
            var rescorer = new ClaimRescorer(
                new MetadataClaimRepository(Database), canonicals, new PriorityCascadeEngine(Config, NullLogger<PriorityCascadeEngine>.Instance), Config, [],
                new CanonicalValueArrayRepository(Database), new SearchIndexRepository(Database), NullLogger<ClaimRescorer>.Instance);
            Rescoring = new FlakyRescorer(rescorer, Stop);
            return new FolderKindChangeRunner(
                configuration, _kinds, FlakyChanges, Ledger, FlakyAssetsProxy.Create(_assets, LibraryHook), canonicals, new IdentityJobRepository(Database), new UnmatchedAssetRepository(Database), Filing,
                _firstCheckService, Rescoring, Rereader, Activity, new MoveOwnedFileCleaner(new AssetPathService(MediaRoot), NullLogger<MoveOwnedFileCleaner>.Instance),
                NullLogger<FolderKindChangeRunner>.Instance, timing: new FolderOrganiseTiming { ProgressInterval = TimeSpan.Zero });
        }

        public string AddLibrary(string name, string kindKey, string area, string mediaType, bool plain = false, bool youtube = false)
        {
            var libraries = Config.LoadLibraries();
            var id = Guid.NewGuid().ToString("D");
            libraries.Libraries.Add(new LibraryFolderConfig
            {
                Id = id,
                Name = name,
                Category = name,
                Kind = LibraryKinds.Catalogued,
                KindKey = kindKey,
                Area = area,
                Presentation = LibraryPresentations.Catalogue,
                MetadataPolicy = plain || youtube ? LibraryMetadataPolicies.LocalOnly : LibraryMetadataPolicies.Enriched,
                YoutubeLookup = youtube,
                MediaTypes = [mediaType],
                Sources = [],
            });
            Config.SaveLibraries(libraries);
            return id;
        }

        public LibrarySourceConfig AddFolder(string libraryId, string name, bool managed, string? path = null)
        {
            path ??= Path.Combine(MediaRoot, "folders", Guid.NewGuid().ToString("N"), name);
            Directory.CreateDirectory(path);
            var libraries = Config.LoadLibraries();
            var source = new LibrarySourceConfig
            {
                Id = Guid.NewGuid().ToString("D"),
                Path = path,
                Notes = name,
                ManagementMode = managed ? LibrarySourceManagementModes.ManagedByTuvima : LibrarySourceManagementModes.ExistingLibrary,
                AccessMode = managed ? LibrarySourceAccessModes.Writable : LibrarySourceAccessModes.ReadOnly,
            };
            libraries.Libraries.Single(library => library.Id == libraryId).Sources.Add(source);
            Config.SaveLibraries(libraries);
            return source;
        }

        /// <summary>A file on disk and registered the way ingestion would: its Work, edition and asset in the folder's library, the file's own title claim, and a looked-up title claim that won.</summary>
        public FileRef AddFile(LibrarySourceConfig folder, string fileName, MediaType mediaType, string title, string? year = null)
        {
            var path = Path.Combine(folder.Path, fileName);
            File.WriteAllText(path, $"{title} bytes");
            var libraryId = Config.LoadLibraries().Libraries.Single(library => library.Sources.Any(source => source.Id == folder.Id)).Id;
            var workId = Guid.NewGuid();
            var editionId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            using var connection = Database.CreateConnection();
            connection.Execute(
                """
                INSERT OR IGNORE INTO metadata_providers (id, name, version, is_enabled) VALUES
                    (@local, 'local_processor', '1', 1), (@tmdb, 'tmdb', '1', 1), (@manual, 'user_manual', '1', 1);
                INSERT INTO works (id, media_type, work_kind, ownership) VALUES (@workId, @mediaType, 'standalone', 'Owned');
                INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status, library_id)
                VALUES (@assetId, @editionId, @hash, @path, 'Normal', @libraryId);
                INSERT INTO metadata_claims (id, entity_id, provider_id, claim_key, claim_value, confidence, claimed_at) VALUES
                    (@c1, @assetId, @local, 'title', @title, 0.6, '2026-01-01'),
                    (@c2, @assetId, @tmdb, 'title', 'The Looked-Up Title', 0.9, '2026-01-02'),
                    (@c3, @workId, @tmdb, 'genre', 'Drama', 0.9, '2026-01-02');
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES
                    (@assetId, 'title', 'The Looked-Up Title', '2026-01-02'),
                    (@assetId, 'media_type', @mediaType, '2026-01-02'),
                    (@workId, 'genre', 'Drama', '2026-01-02');
                """,
                new
                {
                    workId,
                    editionId,
                    assetId,
                    path,
                    libraryId,
                    title,
                    mediaType = mediaType.ToString(),
                    hash = assetId.ToString("N"),
                    c1 = Guid.NewGuid(),
                    c2 = Guid.NewGuid(),
                    c3 = Guid.NewGuid(),
                    local = WellKnownProviders.LocalProcessor,
                    tmdb = WellKnownProviders.Tmdb,
                    manual = WellKnownProviders.UserManual,
                });
            if (year is not null)
            {
                connection.Execute(
                    "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@assetId, 'year', @year, '2026-01-01');",
                    new { assetId, year });
            }

            return new FileRef(assetId, workId, path, title);
        }

        public async Task<Account> CreateAccountAsync(string email, string name, IEnumerable<Guid> hidden)
        {
            var profileId = Guid.NewGuid();
            var account = new Account
            {
                Id = Guid.NewGuid(),
                Email = email,
                NormalizedEmail = email.ToUpperInvariant(),
                IsEnabled = true,
                AuthorizationVersion = 1,
                CreatedAt = Now,
                UpdatedAt = Now,
            };
            await _accounts.CreateAccountAsync(
                account,
                new AccountProfileGrant
                {
                    AccountId = account.Id,
                    ProfileId = profileId,
                    IsDefault = true,
                    IsEnabled = true,
                    AuthorizationVersion = 1,
                    GrantedAt = Now,
                },
                new HashSet<AccountFeatureId> { AccountFeatureId.Watch },
                hidden.ToHashSet(),
                newProfile: new Profile { Id = profileId, DisplayName = name, Role = ProfileRole.StandardUser, CreatedAt = Now });
            return account;
        }

        public void Execute(string sql, object? args = null)
        {
            using var connection = Database.CreateConnection();
            connection.Execute(sql, args);
        }

        public T Scalar<T>(string sql, object? args = null)
        {
            using var connection = Database.CreateConnection();
            return connection.ExecuteScalar<T>(sql, args)!;
        }

        public List<T> Rows<T>(string sql, object? args = null)
        {
            using var connection = Database.CreateConnection();
            return [.. connection.Query<T>(sql, args)];
        }

        public async Task RunWorkerUntilFinishedAsync(string operationId, int timeoutSeconds = 30)
        {
            var id = Guid.Parse(operationId);
            await Worker.StartAsync(CancellationToken.None);
            try
            {
                var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
                while (!await IsFinishedAsync(id) && DateTimeOffset.UtcNow < deadline)
                {
                    await Task.Delay(25);
                }

                Assert.True(await IsFinishedAsync(id), "The folder job did not finish in time.");
            }
            finally
            {
                await Worker.StopAsync(CancellationToken.None);
            }
        }

        private async Task<bool> IsFinishedAsync(Guid id)
        {
            var operation = await Operations.GetByIdAsync(id);
            return operation is not null && operation.Status is MediaOperationStatus.Succeeded
                or MediaOperationStatus.Cancelled or MediaOperationStatus.FailedTerminal;
        }

        public void Dispose()
        {
            try
            {
                Config.Dispose();
                Database.Dispose();
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Removed by the next run's cleanup of the tuvima-kind-change folder.
                _ = ex;
            }
        }
    }
}
