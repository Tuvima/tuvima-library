using Dapper;
using MediaEngine.Api.Realtime;
using MediaEngine.Api.Services.Settings;
using MediaEngine.Api.Services.Vault;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Identity;
using MediaEngine.Identity.Contracts;
using MediaEngine.Storage;
using MediaEngine.Storage.Configuration;
using MediaEngine.TestSupport;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

// Adding a folder to the Vault (Videos spec D45, D25, D17): the administrator's own profile PIN is asked for again, the folder must
// exist and be free, and once added it is part of the Vault library in libraries.json, read-only, and counts as the Vault to the
// part of the Engine that decides who may hear about a path. Nothing logs a path or a PIN.
public sealed class VaultFolderAttachmentServiceTests : IDisposable
{
    private const string AdminPin = "2468";
    private const string WrongPin = "00000";
    private const string MoviesLibraryId = "6f1c1f0e-0000-4000-8000-000000000001";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima_vault_folder_{Guid.NewGuid():N}");
    private readonly string _vaultFolder;
    private readonly string _moviesFolder;
    private readonly DatabaseConnection _database;
    private readonly VaultLibraryRepository _libraries;
    private readonly ConfigurationDirectoryLoader _configuration;
    private readonly FirstPartyIdentityService _identity;
    private readonly VaultAdministrationService _administration;
    private readonly VaultFolderAttachmentService _service;
    private readonly VaultPathClassifier _classifier;
    private readonly CapturingLogger _log = new();
    private readonly Guid _admin = Guid.NewGuid();

    public VaultFolderAttachmentServiceTests()
    {
        _vaultFolder = Path.Combine(_root, "Adult");
        _moviesFolder = Path.Combine(_root, "Movies");
        Directory.CreateDirectory(_vaultFolder);
        Directory.CreateDirectory(_moviesFolder);
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(Path.Combine(_root, "library.db"));
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _libraries = new VaultLibraryRepository(_database);
        _configuration = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
        _configuration.SaveLibraries(new LibrariesConfiguration
        {
            StorageLocations = [new ServerStorageLocationConfig { Id = "media", Label = "Media", Path = _root, AllowWrite = true }],
        });

        var nsfw = new NsfwAccessRepository(_database);
        var identities = new IdentityRepository(_database);
        var profiles = new ProfileRepository(_database);
        var clock = TimeProvider.System;
        _identity = new FirstPartyIdentityService(
            identities, new AccountRepository(_database), profiles,
            new PasswordHasher<AccountCredential>(), new PasswordHasher<ProfileCredential>(), clock, new FixedPolicy(),
            nsfwAccess: nsfw);
        _administration = new VaultAdministrationService(_libraries, nsfw, profiles, identities, _identity, clock, NullLogger<VaultAdministrationService>.Instance);
        _service = new VaultFolderAttachmentService(
            _administration, _libraries, _configuration, new ServerFolderBrowserService(_configuration, NullLogger<ServerFolderBrowserService>.Instance), _log);
        _classifier = new VaultPathClassifier(_libraries, _configuration);

        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO profiles(id,display_name,avatar_color,role,content_limit,created_at) VALUES(@id,'Administrator','#7C4DFF','Administrator',NULL,@now);",
            new { id = _admin, now = DateTimeOffset.UtcNow.ToString("O") });
    }

    public void Dispose()
    {
        _configuration.Dispose();
        _database.Dispose();
        TestTemp.ReleaseSqlitePool(Path.Combine(_root, "library.db"));
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // ── Allowed ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WithTheRightPin_AnExistingFolderJoinsTheVaultLibrary_AsAReadOnlyWatchedFolder()
    {
        var vault = await NewVaultLibraryAsync();

        var outcome = await _service.AttachAsync(_admin, vault.LibraryId, _vaultFolder, AdminPin, default);

        Assert.Equal(VaultFolderAttachmentStatus.Attached, outcome.Status);
        Assert.Equal(vault.LibraryId, outcome.Library!.LibraryId);
        var entry = Assert.Single(_configuration.LoadLibraries().Libraries);
        Assert.Equal(vault.LibraryId, entry.Id);
        Assert.Equal(LibraryAreas.Watch, entry.Area);
        Assert.Equal(LibraryPresentations.Catalogue, entry.Presentation);
        Assert.Equal(["Videos"], entry.MediaTypes);
        Assert.False(entry.YoutubeLookup);
        var source = Assert.Single(entry.Sources);
        Assert.Equal(Path.GetFullPath(_vaultFolder), Path.GetFullPath(source.Path));
        Assert.Equal(LibrarySourceManagementModes.ExistingLibrary, source.ManagementMode);
        Assert.False(source.IsWritable);
        Assert.True(source.IncludeSubdirectories);
    }

    [Fact]
    public async Task OnceAdded_TheFolderAndEverythingInsideIt_AreVaultPaths_AndNothingBesideItIs()
    {
        var vault = await NewVaultLibraryAsync();
        Assert.False(await _classifier.IsVaultPathAsync(Path.Combine(_vaultFolder, "Clips", "one.mkv")));

        await _service.AttachAsync(_admin, vault.LibraryId, _vaultFolder, AdminPin, default);

        Assert.True(await _classifier.IsVaultPathAsync(_vaultFolder));
        Assert.True(await _classifier.IsVaultPathAsync(Path.Combine(_vaultFolder, "Clips", "one.mkv")));
        Assert.False(await _classifier.IsVaultPathAsync(Path.Combine(_root, "AdultToo", "one.mkv")));
        Assert.False(await _classifier.IsVaultPathAsync(Path.Combine(_moviesFolder, "film.mkv")));
    }

    [Fact]
    public async Task ASecondFolder_JoinsTheSameVaultEntry_AndOrdinaryLibrariesAreLeftAlone()
    {
        SeedOrdinaryMoviesLibrary();
        var vault = await NewVaultLibraryAsync();
        var second = Path.Combine(_root, "MoreAdult");
        Directory.CreateDirectory(second);

        await _service.AttachAsync(_admin, vault.LibraryId, _vaultFolder, AdminPin, default);
        var outcome = await _service.AttachAsync(_admin, vault.LibraryId, second, AdminPin, default);

        Assert.Equal(VaultFolderAttachmentStatus.Attached, outcome.Status);
        var settings = _configuration.LoadLibraries();
        Assert.Equal(2, settings.Libraries.Count);
        Assert.Equal(2, settings.Libraries.Single(entry => entry.Id == vault.LibraryId).Sources.Count);
        var movies = settings.Libraries.Single(entry => entry.Id == MoviesLibraryId);
        Assert.Equal(Path.GetFullPath(_moviesFolder), Path.GetFullPath(Assert.Single(movies.Sources).Path));
        Assert.False(await _classifier.IsVaultPathAsync(Path.Combine(_moviesFolder, "film.mkv")));
        Assert.True(await _classifier.IsVaultPathAsync(Path.Combine(second, "x.mkv")));
    }

    // ── Refused ─────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(WrongPin)]
    [InlineData("")]
    [InlineData(null)]
    public async Task AWrongOrMissingPin_AddsNothing_AndIsAskedAgain(string? pin)
    {
        var vault = await NewVaultLibraryAsync();

        await Assert.ThrowsAsync<ProfilePinRequiredException>(() => _service.AttachAsync(_admin, vault.LibraryId, _vaultFolder, pin, default));

        Assert.Empty(_configuration.LoadLibraries().Libraries);
        Assert.False(await _classifier.IsVaultPathAsync(_vaultFolder));
    }

    [Fact]
    public async Task AWrongPin_NeverTellsWhetherTheFolderExists()
    {
        var vault = await NewVaultLibraryAsync();

        await Assert.ThrowsAsync<ProfilePinRequiredException>(() => _service.AttachAsync(_admin, vault.LibraryId, Path.Combine(_root, "NoSuchFolder"), WrongPin, default));
    }

    [Fact]
    public async Task AnAdministratorWithoutAProfilePin_CannotAddAFolder()
    {
        var vault = await NewVaultLibraryAsync();
        await _identity.SetProfilePinAsync(_admin, null);

        await Assert.ThrowsAsync<VaultAdminPinNotSetException>(() => _service.AttachAsync(_admin, vault.LibraryId, _vaultFolder, AdminPin, default));

        Assert.Empty(_configuration.LoadLibraries().Libraries);
    }

    [Fact]
    public async Task AFolderThatIsNotThere_AddsNothing_AndSaysSoWithoutNamingIt()
    {
        var vault = await NewVaultLibraryAsync();
        var missing = Path.Combine(_root, "NoSuchFolder");

        var outcome = await _service.AttachAsync(_admin, vault.LibraryId, missing, AdminPin, default);

        Assert.Equal(VaultFolderAttachmentStatus.FolderNotFound, outcome.Status);
        Assert.Equal(VaultFolderAttachmentService.FolderNotFoundMessage, outcome.Message);
        Assert.DoesNotContain("NoSuchFolder", outcome.Message!, StringComparison.Ordinal);
        Assert.Empty(_configuration.LoadLibraries().Libraries);
        Assert.False(await _classifier.IsVaultPathAsync(missing));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankFolder_IsAFolderThatIsNotThere(string folder)
    {
        var vault = await NewVaultLibraryAsync();

        var outcome = await _service.AttachAsync(_admin, vault.LibraryId, folder, AdminPin, default);

        Assert.Equal(VaultFolderAttachmentStatus.FolderNotFound, outcome.Status);
        Assert.Empty(_configuration.LoadLibraries().Libraries);
    }

    [Fact]
    public async Task ARelativePath_IsNotAllowed()
    {
        var vault = await NewVaultLibraryAsync();

        var outcome = await _service.AttachAsync(_admin, vault.LibraryId, "Adult/Clips", AdminPin, default);

        Assert.NotEqual(VaultFolderAttachmentStatus.Attached, outcome.Status);
        Assert.Empty(_configuration.LoadLibraries().Libraries);
    }

    [Fact]
    public async Task AFolderThatAnotherLibraryAlreadyUses_IsNotAllowed_AndTheOrdinaryLibraryStaysOrdinary()
    {
        SeedOrdinaryMoviesLibrary();
        var vault = await NewVaultLibraryAsync();

        var same = await _service.AttachAsync(_admin, vault.LibraryId, _moviesFolder, AdminPin, default);
        Directory.CreateDirectory(Path.Combine(_moviesFolder, "Inside"));
        var inside = await _service.AttachAsync(_admin, vault.LibraryId, Path.Combine(_moviesFolder, "Inside"), AdminPin, default);

        Assert.Equal(VaultFolderAttachmentStatus.NotAllowed, same.Status);
        Assert.Equal(VaultFolderAttachmentStatus.NotAllowed, inside.Status);
        Assert.Equal(VaultFolderAttachmentService.FolderNotAllowedMessage, same.Message);
        var only = Assert.Single(_configuration.LoadLibraries().Libraries);
        Assert.Equal(MoviesLibraryId, only.Id);
        Assert.False(await _classifier.IsVaultPathAsync(Path.Combine(_moviesFolder, "film.mkv")));
    }

    [Fact]
    public async Task ALibraryThatIsNotAVaultLibrary_IsNotFound_EvenWithTheRightPin()
    {
        SeedOrdinaryMoviesLibrary();

        var ordinary = await _service.AttachAsync(_admin, MoviesLibraryId, _vaultFolder, AdminPin, default);
        var unknown = await _service.AttachAsync(_admin, Guid.NewGuid().ToString("D"), _vaultFolder, AdminPin, default);

        Assert.Equal(VaultFolderAttachmentStatus.LibraryNotFound, ordinary.Status);
        Assert.Equal(VaultFolderAttachmentStatus.LibraryNotFound, unknown.Status);
        Assert.Single(_configuration.LoadLibraries().Libraries);
    }

    [Fact]
    public async Task NoLogLine_NamesTheFolderOrThePin()
    {
        var vault = await NewVaultLibraryAsync();

        await _service.AttachAsync(_admin, vault.LibraryId, _vaultFolder, AdminPin, default);
        await _service.AttachAsync(_admin, vault.LibraryId, Path.Combine(_root, "NoSuchFolder"), AdminPin, default);
        await Assert.ThrowsAsync<ProfilePinRequiredException>(() => _service.AttachAsync(_admin, vault.LibraryId, _vaultFolder, WrongPin, default));

        Assert.NotEmpty(_log.Lines);
        Assert.All(_log.Lines, line =>
        {
            Assert.DoesNotContain("Adult", line, StringComparison.Ordinal);
            Assert.DoesNotContain("NoSuchFolder", line, StringComparison.Ordinal);
            Assert.DoesNotContain(AdminPin, line, StringComparison.Ordinal);
            Assert.DoesNotContain(WrongPin, line, StringComparison.Ordinal);
        });
    }

    // ── Settings screens never see the Vault ───────────────────────────────────────────────────

    [Fact]
    public async Task TheLibrarySettingsLeaveTheVaultOut_KeepItWhenSaved_AndDoNotCountItAsASecondVideosLibrary()
    {
        SeedOrdinaryMoviesLibrary();
        var vault = await NewVaultLibraryAsync();
        await _service.AttachAsync(_admin, vault.LibraryId, _vaultFolder, AdminPin, default);
        var ids = await _libraries.GetIdsAsync();
        var current = _configuration.LoadLibraries();

        var shown = VaultLibrarySettings.WithoutVault(current, ids);
        Assert.Equal(MoviesLibraryId, Assert.Single(shown.Libraries).Id);

        // A settings screen sends back what it was shown (even with a Videos library of its own); the Vault entry survives untouched.
        var sent = VaultLibrarySettings.WithoutVault(current, ids);
        sent = new LibrariesConfiguration { StorageLocations = sent.StorageLocations, Libraries = [.. sent.Libraries] };
        sent.Libraries.Add(new LibraryFolderConfig { Id = Guid.NewGuid().ToString("D"), Name = "Videos", Category = "Videos", MediaTypes = ["Videos"] });
        VaultLibrarySettings.KeepVault(current, sent, ids);
        Assert.Equal(3, sent.Libraries.Count);
        Assert.Single(sent.Libraries, library => library.Id == vault.LibraryId && library.Sources.Count == 1);
        Assert.Empty(LibraryKindRules.LibraryProblems(VaultLibrarySettings.WithoutVault(sent, ids), LibraryKindsConfiguration.BuiltIn()));
    }

    // ── Setup ───────────────────────────────────────────────────────────────────────────────────

    private async Task<VaultLibrary> NewVaultLibraryAsync()
    {
        await _identity.SetProfilePinAsync(_admin, AdminPin);
        return await _administration.CreateLibraryAsync(_admin, VaultLookupSources.None, null, true, AdminPin, default);
    }

    private void SeedOrdinaryMoviesLibrary()
    {
        var settings = _configuration.LoadLibraries();
        settings.Libraries.Add(new LibraryFolderConfig
        {
            Id = MoviesLibraryId,
            Name = "Movies",
            Category = "Movies",
            Area = LibraryAreas.Watch,
            MediaTypes = ["Movies"],
            Sources = [new LibrarySourceConfig { Id = Guid.NewGuid().ToString("D"), Path = _moviesFolder, ManagementMode = LibrarySourceManagementModes.ExistingLibrary }],
        });
        _configuration.SaveLibraries(settings);
    }

    private sealed class FixedPolicy : IAuthenticationPolicyProvider
    {
        public AuthSettings GetCurrent() => new();
    }

    private sealed class CapturingLogger : ILogger<VaultFolderAttachmentService>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Lines.Add(formatter(state, exception));
            if (exception is not null)
            {
                Lines.Add(exception.ToString());
            }
        }
    }
}
