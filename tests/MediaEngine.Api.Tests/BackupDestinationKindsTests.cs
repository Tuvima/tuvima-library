using System.Net;
using System.Security.Cryptography;
using System.Text;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Backups;
using MediaEngine.Contracts.Backups;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>A stand-in for the machine key: it only needs to turn a login into something that is not the login.</summary>
internal sealed class ReversingSecretStore : ISecretStore
{
    public string Encrypt(string plaintext) => "enc:" + new string(plaintext.Reverse().ToArray());

    public string Decrypt(string ciphertext) => new string(ciphertext["enc:".Length..].Reverse().ToArray());
}

/// <summary>
/// The first backup destinations (Developer tools spec WP26): another folder and a network share, S3-compatible storage and WebDAV.
/// The web ones are exercised against an in-memory stand-in for a server, so each request's shape and login are checked.
/// </summary>
public sealed class BackupDestinationKindsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima-backup-kinds-{Guid.NewGuid():N}");
    private readonly DatabaseConnection _db;
    private readonly BackupDestinationRepository _repository;
    private readonly BackupSettingsAccessor _settings;
    private readonly BackupSettingsManager _manager;
    private readonly BackupOrchestrator _orchestrator;
    private readonly ReversingSecretStore _secrets = new();

    public BackupDestinationKindsTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        File.WriteAllText(Path.Combine(_root, "config", "core.json"), "{\"server_name\":\"Home\"}");
        DapperConfiguration.Configure();
        _db = new DatabaseConnection(Path.Combine(_root, "library.db"));
        _db.InitializeSchema();
        _db.RunStartupChecks();
        var backups = BackupTestKit.Service(_db, Path.Combine(_root, "config"), Path.Combine(_root, "backups"));
        _repository = new BackupDestinationRepository(_db);
        var clock = TimeProvider.System;
        _settings = new BackupSettingsAccessor(_repository, clock);
        var configured = new ConfiguredBackupDestinations(_settings, _secrets, new BackupHttp());
        _orchestrator = new BackupOrchestrator(
            backups, [new LocalFolderBackupDestination(backups)], configured, _settings, _repository, new NullPublisher(), clock, NullLogger<BackupOrchestrator>.Instance);
        _manager = new BackupSettingsManager(_settings, _orchestrator, _repository, _secrets, clock);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task AFolder_SendsListsFetchesAndDeletes_AndNeverShowsAHalfWrittenFile()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "drive")).FullName;
        var destination = new FolderBackupDestination("folder-1", "Spare drive", folder, isShare: false);
        File.WriteAllText(Path.Combine(folder, "tuvima-backup-half.zip.part"), "partial");

        Assert.True((await destination.TestAsync(CancellationToken.None)).Success);
        await using (var content = new MemoryStream([1, 2, 3, 4]))
        {
            await destination.SendAsync("tuvima-backup-a.zip", content, 4, CancellationToken.None);
        }

        var held = Assert.Single(await destination.ListAsync(CancellationToken.None));
        Assert.Equal("tuvima-backup-a.zip", held.Name);
        Assert.Equal(4, held.SizeBytes);
        await using (var fetched = await destination.FetchAsync("tuvima-backup-a.zip", CancellationToken.None))
        {
            Assert.Equal(4, fetched.Length);
        }

        await destination.DeleteAsync("tuvima-backup-a.zip", CancellationToken.None);
        Assert.Empty(await destination.ListAsync(CancellationToken.None));
        await Assert.ThrowsAsync<BackupDestinationException>(() => destination.FetchAsync("..\\evil", CancellationToken.None));
    }

    [Fact]
    public async Task AFolderThatIsMissing_SaysSoInPlainWords()
    {
        var destination = new FolderBackupDestination("share-1", "NAS", Path.Combine(_root, "not-there"), isShare: true);

        var result = await destination.TestAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("network share", result.Message);
    }

    [Fact]
    public async Task ABackupRunToAFolder_IsCopied_Checked_Trimmed_AndRestorable()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "drive")).FullName;
        Assert.Null(await _manager.SaveDestinationAsync(new SaveBackupDestinationRequest { Kind = BackupDestinationKinds.Folder, Name = "Spare drive", Location = folder }, CancellationToken.None));
        Assert.Null(await _manager.UpdateAsync(new UpdateBackupSettingsRequest { KeepLast = 1 }, CancellationToken.None));
        var added = Assert.Single((await _manager.GetAsync(CancellationToken.None)).Destinations, item => item.Removable);

        var first = await _orchestrator.RunAsync(CancellationToken.None);
        await Task.Delay(1100);
        var second = await _orchestrator.RunAsync(CancellationToken.None);

        Assert.True(first.AllOk && second.AllOk);
        var file = Assert.Single(Directory.GetFiles(folder));
        Assert.EndsWith(".zip", file);
        Assert.Equal((byte)'P', File.ReadAllBytes(file)[0]); // a plain zip
        var scheduled = await _orchestrator.RestoreFromDestinationAsync(_orchestrator.Find(added.Id)!, Path.GetFileName(file), CancellationToken.None);
        Assert.True(scheduled.Scheduled);
    }

    [Theory]
    [InlineData("s3", "https://s3.example.com", "")]
    [InlineData("webdav", "https://dav.example.com/backups", "other.example.com")]
    public async Task AWebDestination_IsOnlySavedOnceTheHostWasConfirmed(string kind, string address, string confirmed)
    {
        var request = new SaveBackupDestinationRequest
        {
            Kind = kind,
            Name = "Cloud",
            Location = address,
            Bucket = "bucket",
            Username = "me",
            Secret = "pw",
            ConfirmedHost = confirmed,
        };

        var problem = await _manager.SaveDestinationAsync(request, CancellationToken.None);

        Assert.NotNull(problem);
        Assert.Contains("example.com", problem);
        Assert.DoesNotContain((await _manager.GetAsync(CancellationToken.None)).Destinations, item => item.Removable);
    }

    [Theory]
    [InlineData("http://storage.example.com")]
    [InlineData("https://me:pw@storage.example.com")]
    [InlineData("https://169.254.169.254")]
    [InlineData("https://[::ffff:169.254.169.254]")]
    [InlineData("http://[2001:db8::1]")]
    [InlineData("ftp://storage.example.com")]
    [InlineData("not an address")]
    public async Task AnUnsafeWebAddress_IsRefused(string address)
    {
        var problem = await _manager.SaveDestinationAsync(new SaveBackupDestinationRequest
        {
            Kind = BackupDestinationKinds.WebDav,
            Name = "Cloud",
            Location = address,
            Username = "me",
            Secret = "pw",
            ConfirmedHost = "storage.example.com",
        }, CancellationToken.None);

        Assert.NotNull(problem);
    }

    [Fact]
    public async Task AWebDestination_KeepsItsLoginEncrypted_AndNeverReturnsIt()
    {
        var problem = await _manager.SaveDestinationAsync(new SaveBackupDestinationRequest
        {
            Kind = BackupDestinationKinds.S3,
            Name = "MinIO at home",
            Location = "http://nas.local:9000",
            Bucket = "backups",
            Username = "AKIDEXAMPLE",
            Secret = "super-secret-value",
            ConfirmedHost = "nas.local",
        }, CancellationToken.None);

        Assert.Null(problem);
        var stored = Assert.Single((await _settings.LoadAsync(CancellationToken.None)).Destinations);
        Assert.NotEqual("super-secret-value", stored.EncryptedSecret);
        var shown = Assert.Single((await _manager.GetAsync(CancellationToken.None)).Destinations, item => item.Removable);
        Assert.Equal("http://nas.local:9000", shown.Location);
        Assert.DoesNotContain("super-secret-value", System.Text.Json.JsonSerializer.Serialize(shown));

        // Editing without a new secret keeps the saved one.
        Assert.Null(await _manager.SaveDestinationAsync(new SaveBackupDestinationRequest
        {
            Id = stored.Id,
            Kind = BackupDestinationKinds.S3,
            Name = "MinIO renamed",
            Location = "http://nas.local:9000",
            Bucket = "backups",
            Username = "AKIDEXAMPLE",
            ConfirmedHost = "nas.local",
        }, CancellationToken.None));
        Assert.Equal(stored.EncryptedSecret, Assert.Single((await _settings.LoadAsync(CancellationToken.None)).Destinations).EncryptedSecret);

        Assert.True(await _manager.RemoveDestinationAsync(stored.Id, CancellationToken.None));
        Assert.Empty((await _settings.LoadAsync(CancellationToken.None)).Destinations);
        Assert.DoesNotContain(stored.Id, (await _settings.LoadAsync(CancellationToken.None)).EnabledDestinations);
    }

    [Fact]
    public async Task EditingAWebDestinationsAddress_NeedsTheLoginAgain_SoASavedLoginNeverFollowsToAnotherServer()
    {
        Assert.Null(await _manager.SaveDestinationAsync(new SaveBackupDestinationRequest
        {
            Kind = BackupDestinationKinds.WebDav,
            Name = "Cloud",
            Location = "https://dav.example.com/a",
            Username = "me",
            Secret = "pw",
            ConfirmedHost = "dav.example.com",
        }, CancellationToken.None));
        var id = Assert.Single((await _settings.LoadAsync(CancellationToken.None)).Destinations).Id;

        var moved = await _manager.SaveDestinationAsync(new SaveBackupDestinationRequest
        {
            Id = id,
            Kind = BackupDestinationKinds.WebDav,
            Name = "Cloud",
            Location = "https://evil.example.net/a",
            Username = "me",
            ConfirmedHost = "evil.example.net",
        }, CancellationToken.None);
        var renamedOnly = await _manager.SaveDestinationAsync(new SaveBackupDestinationRequest
        {
            Id = id,
            Kind = BackupDestinationKinds.WebDav,
            Name = "Cloud renamed",
            Location = "https://dav.example.com/other-folder",
            Username = "me",
            ConfirmedHost = "dav.example.com",
        }, CancellationToken.None);

        Assert.Contains("password again", moved);
        Assert.Null(renamedOnly);
    }

    [Fact]
    public async Task AnS3Listing_ThatNeverEnds_StopsInsteadOfLoopingForever()
    {
        var destination = new S3BackupDestination(
            new ConfiguredDestination { Id = "s3-1", Name = "Bucket", Location = "https://s3.example.com", Bucket = "bucket", Username = "AKID" }, "secret", new EndlessListing());

        await Assert.ThrowsAsync<BackupDestinationException>(() => destination.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AnXmlAnswerThatDefinesEntities_IsRefused()
    {
        var destination = new WebDavBackupDestination(
            new ConfiguredDestination { Id = "webdav-1", Name = "Nextcloud", Location = "https://dav.example.com/dav", Username = "me" }, "pw", new HostileXml());

        await Assert.ThrowsAnyAsync<Exception>(() => destination.ListAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("relative/folder")]
    [InlineData("")]
    public async Task AFolderThatIsNotAFullPath_IsRefused(string path)
    {
        var problem = await _manager.SaveDestinationAsync(new SaveBackupDestinationRequest { Kind = BackupDestinationKinds.Share, Name = "NAS", Location = path }, CancellationToken.None);

        Assert.Contains("full path", problem);
    }

    [Fact]
    public async Task WebDav_SendsWithTheLogin_ListsFetchesAndDeletes_AndMakesAMissingFolder()
    {
        var server = new FakeWebDavServer("me", "pw");
        var destination = new WebDavBackupDestination(
            new ConfiguredDestination { Id = "webdav-1", Name = "Nextcloud", Location = "https://dav.example.com/remote.php/dav/files/me/Backups", Username = "me" }, "pw", server);

        Assert.True((await destination.TestAsync(CancellationToken.None)).Success);
        await using (var content = new MemoryStream([9, 8, 7]))
        {
            await destination.SendAsync("tuvima-backup-a.zip", content, 3, CancellationToken.None);
        }

        Assert.Contains("MKCOL", server.Methods);
        var held = Assert.Single(await destination.ListAsync(CancellationToken.None));
        Assert.Equal(("tuvima-backup-a.zip", 3), (held.Name, held.SizeBytes));
        await using (var fetched = await destination.FetchAsync("tuvima-backup-a.zip", CancellationToken.None))
        {
            using var copy = new MemoryStream();
            await fetched.CopyToAsync(copy);
            Assert.Equal(new byte[] { 9, 8, 7 }, copy.ToArray());
        }

        await destination.DeleteAsync("tuvima-backup-a.zip", CancellationToken.None);
        Assert.Empty(await destination.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task WebDav_WithAWrongPassword_SaysTheLoginWasRefused_WithoutTheAddressOrPassword()
    {
        var destination = new WebDavBackupDestination(
            new ConfiguredDestination { Id = "webdav-1", Name = "Nextcloud", Location = "https://dav.example.com/dav", Username = "me" }, "wrong", new FakeWebDavServer("me", "pw"));

        var result = await destination.TestAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("login", result.Message);
        Assert.DoesNotContain("wrong", result.Message);
        Assert.DoesNotContain("dav.example.com", result.Message);
    }

    [Fact]
    public async Task S3_SignsEveryRequest_UsesThePrefix_AndPagesThroughALongListing()
    {
        var server = new FakeS3Server("AKIDEXAMPLE", "bucket", pageSize: 1);
        var destination = new S3BackupDestination(
            new ConfiguredDestination { Id = "s3-1", Name = "Bucket", Location = "https://s3.example.com", Bucket = "bucket", Region = "eu-west-1", Prefix = "library/", Username = "AKIDEXAMPLE" }, "secret", server);

        Assert.True((await destination.TestAsync(CancellationToken.None)).Success);
        foreach (var name in new[] { "tuvima-backup-a.zip", "tuvima-backup-b.zip" })
        {
            await using var content = new MemoryStream(Encoding.UTF8.GetBytes(name));
            await destination.SendAsync(name, content, content.Length, CancellationToken.None);
        }

        var held = await destination.ListAsync(CancellationToken.None);
        Assert.Equal(["tuvima-backup-a.zip", "tuvima-backup-b.zip"], held.Select(item => item.Name).Order());
        Assert.All(server.Authorizations, header =>
        {
            Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/", header);
            Assert.Contains("/eu-west-1/s3/aws4_request", header);
            Assert.Contains("SignedHeaders=host;x-amz-content-sha256;x-amz-date", header);
        });
        Assert.Contains("library/tuvima-backup-a.zip", server.Keys);
        await using (var fetched = await destination.FetchAsync("tuvima-backup-b.zip", CancellationToken.None))
        {
            using var copy = new MemoryStream();
            await fetched.CopyToAsync(copy);
            Assert.Equal("tuvima-backup-b.zip", Encoding.UTF8.GetString(copy.ToArray()));
        }

        await destination.DeleteAsync("tuvima-backup-a.zip", CancellationToken.None);
        Assert.Single(await destination.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task S3_SendsAPayloadHashThatMatchesTheBytes()
    {
        var server = new FakeS3Server("AKIDEXAMPLE", "bucket", pageSize: 100);
        var destination = new S3BackupDestination(
            new ConfiguredDestination { Id = "s3-1", Name = "Bucket", Location = "https://s3.example.com", Bucket = "bucket", Username = "AKIDEXAMPLE" }, "secret", server);

        await using var content = new MemoryStream([5, 6, 7, 8]);
        await destination.SendAsync("tuvima-backup-a.zip", content, 4, CancellationToken.None);

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData([5, 6, 7, 8])), server.LastPayloadHash);
    }

    [Fact]
    public async Task S3_WithARefusedLogin_SaysSo()
    {
        var server = new FakeS3Server("someone-else", "bucket", pageSize: 100);
        var destination = new S3BackupDestination(
            new ConfiguredDestination { Id = "s3-1", Name = "Bucket", Location = "https://s3.example.com", Bucket = "bucket", Username = "AKIDEXAMPLE" }, "secret", server);

        var result = await destination.TestAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("login", result.Message);
    }

    /// <summary>
    /// Runs the real signing against an S3-compatible server that checks signatures (MinIO, or the moto test server). Set
    /// <c>TUVIMA_TEST_S3_ENDPOINT</c>, <c>TUVIMA_TEST_S3_BUCKET</c>, <c>TUVIMA_TEST_S3_KEY</c> and <c>TUVIMA_TEST_S3_SECRET</c>; without them there is nothing to talk to and the test does nothing.
    /// </summary>
    [Fact]
    [Trait("Category", "LiveProvider")]
    public async Task S3_AgainstARealServer_SendsListsFetchesAndDeletes()
    {
        var endpoint = Environment.GetEnvironmentVariable("TUVIMA_TEST_S3_ENDPOINT");
        if (string.IsNullOrEmpty(endpoint))
        {
            return;
        }

        var destination = new S3BackupDestination(
            new ConfiguredDestination
            {
                Id = "s3-live",
                Name = "Live",
                Location = endpoint,
                Bucket = Environment.GetEnvironmentVariable("TUVIMA_TEST_S3_BUCKET"),
                Prefix = "folder with space",
                Region = "us-east-1",
                Username = Environment.GetEnvironmentVariable("TUVIMA_TEST_S3_KEY"),
            },
            Environment.GetEnvironmentVariable("TUVIMA_TEST_S3_SECRET") ?? string.Empty,
            new BackupHttp());

        var test = await destination.TestAsync(CancellationToken.None);
        Assert.True(test.Success, test.Message);
        var bytes = new byte[300_000];
        Random.Shared.NextBytes(bytes);
        await using (var content = new MemoryStream(bytes))
        {
            await destination.SendAsync("tuvima-backup-live.zip", content, bytes.Length, CancellationToken.None);
        }

        var held = Assert.Single(await destination.ListAsync(CancellationToken.None), item => item.Name == "tuvima-backup-live.zip");
        Assert.Equal(bytes.Length, held.SizeBytes);
        await using (var fetched = await destination.FetchAsync("tuvima-backup-live.zip", CancellationToken.None))
        {
            Assert.Equal(SHA256.HashData(bytes), await SHA256.HashDataAsync(fetched));
        }

        await destination.DeleteAsync("tuvima-backup-live.zip", CancellationToken.None);
        Assert.DoesNotContain(await destination.ListAsync(CancellationToken.None), item => item.Name == "tuvima-backup-live.zip");
    }

    /// <summary>
    /// Runs the real requests against a WebDAV server. Set <c>TUVIMA_TEST_WEBDAV_URL</c>, <c>TUVIMA_TEST_WEBDAV_USER</c> and <c>TUVIMA_TEST_WEBDAV_PASSWORD</c>;
    /// without them there is nothing to talk to and the test does nothing.
    /// </summary>
    [Fact]
    [Trait("Category", "LiveProvider")]
    public async Task WebDav_AgainstARealServer_SendsListsFetchesAndDeletes()
    {
        var url = Environment.GetEnvironmentVariable("TUVIMA_TEST_WEBDAV_URL");
        if (string.IsNullOrEmpty(url))
        {
            return;
        }

        var destination = new WebDavBackupDestination(
            new ConfiguredDestination { Id = "webdav-live", Name = "Live", Location = url, Username = Environment.GetEnvironmentVariable("TUVIMA_TEST_WEBDAV_USER") },
            Environment.GetEnvironmentVariable("TUVIMA_TEST_WEBDAV_PASSWORD") ?? string.Empty,
            new BackupHttp());

        var test = await destination.TestAsync(CancellationToken.None);
        Assert.True(test.Success, test.Message);
        var bytes = new byte[300_000];
        Random.Shared.NextBytes(bytes);
        await using (var content = new MemoryStream(bytes))
        {
            await destination.SendAsync("tuvima-backup-live.zip", content, bytes.Length, CancellationToken.None);
        }

        var held = Assert.Single(await destination.ListAsync(CancellationToken.None), item => item.Name == "tuvima-backup-live.zip");
        Assert.Equal(bytes.Length, held.SizeBytes);
        await using (var fetched = await destination.FetchAsync("tuvima-backup-live.zip", CancellationToken.None))
        {
            Assert.Equal(SHA256.HashData(bytes), await SHA256.HashDataAsync(fetched));
        }

        await destination.DeleteAsync("tuvima-backup-live.zip", CancellationToken.None);
        Assert.DoesNotContain(await destination.ListAsync(CancellationToken.None), item => item.Name == "tuvima-backup-live.zip");
    }

    private sealed class EndlessListing : IBackupHttp
    {
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><IsTruncated>true</IsTruncated><NextContinuationToken>same</NextContinuationToken></ListBucketResult>", Encoding.UTF8, "application/xml"),
            });
    }

    private sealed class HostileXml : IBackupHttp
    {
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage((HttpStatusCode)207)
            {
                Content = new StringContent("<?xml version=\"1.0\"?><!DOCTYPE d [<!ENTITY a \"aaaa\">]><d:multistatus xmlns:d=\"DAV:\">&a;</d:multistatus>", Encoding.UTF8, "application/xml"),
            });
    }

    private sealed class NullPublisher : INotificationPublisher
    {
        public void Publish(NotificationRequest request)
        {
        }
    }

    private sealed class FakeWebDavServer(string user, string password) : IBackupHttp
    {
        private readonly Dictionary<string, byte[]> _files = [];
        private bool _folderExists;

        public List<string> Methods { get; } = [];

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Methods.Add(request.Method.Method);
            var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
            if (request.Headers.Authorization?.Parameter != expected)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            var path = request.RequestUri!.AbsolutePath;
            var name = path.TrimEnd('/').Split('/')[^1];
            var isFolder = !name.EndsWith(".zip", StringComparison.Ordinal);
            switch (request.Method.Method)
            {
                case "MKCOL":
                    _folderExists = true;
                    return new HttpResponseMessage(HttpStatusCode.Created);
                case "PUT":
                    if (!_folderExists)
                    {
                        return new HttpResponseMessage(HttpStatusCode.Conflict);
                    }

                    _files[name] = await request.Content!.ReadAsByteArrayAsync(ct);
                    return new HttpResponseMessage(HttpStatusCode.Created);
                case "GET":
                    return _files.TryGetValue(name, out var bytes)
                        ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                        : new HttpResponseMessage(HttpStatusCode.NotFound);
                case "DELETE":
                    _files.Remove(name);
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                case "PROPFIND" when isFolder:
                    var entries = string.Concat(_files.Select(item =>
                        $"<d:response><d:href>{path.TrimEnd('/')}/{item.Key}</d:href><d:propstat><d:prop><d:resourcetype/><d:getcontentlength>{item.Value.Length}</d:getcontentlength><d:getlastmodified>Wed, 21 Oct 2026 12:00:00 GMT</d:getlastmodified></d:prop></d:propstat></d:response>"));
                    var body = $"<?xml version=\"1.0\"?><d:multistatus xmlns:d=\"DAV:\"><d:response><d:href>{path}</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop></d:propstat></d:response>{entries}</d:multistatus>";
                    return new HttpResponseMessage((HttpStatusCode)207) { Content = new StringContent(body, Encoding.UTF8, "application/xml") };
                default:
                    return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            }
        }
    }

    private sealed class FakeS3Server(string accessKey, string bucket, int pageSize) : IBackupHttp
    {
        private readonly SortedDictionary<string, byte[]> _objects = new(StringComparer.Ordinal);

        public List<string> Authorizations { get; } = [];

        public IReadOnlyCollection<string> Keys => _objects.Keys;

        public string? LastPayloadHash { get; private set; }

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var authorization = request.Headers.GetValues("Authorization").Single();
            Authorizations.Add(authorization);
            if (!authorization.Contains($"Credential={accessKey}/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            }

            var segments = request.RequestUri!.AbsolutePath.TrimStart('/').Split('/', 2);
            if (segments[0] != bucket)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            var key = segments.Length > 1 ? Uri.UnescapeDataString(segments[1]) : string.Empty;
            switch (request.Method.Method)
            {
                case "PUT":
                    LastPayloadHash = request.Headers.GetValues("x-amz-content-sha256").Single();
                    var bytes = await request.Content!.ReadAsByteArrayAsync(ct);
                    Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), LastPayloadHash);
                    _objects[key] = bytes;
                    return new HttpResponseMessage(HttpStatusCode.OK);
                case "DELETE":
                    _objects.Remove(key);
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                case "GET" when key.Length > 0:
                    return _objects.TryGetValue(key, out var found)
                        ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(found) }
                        : new HttpResponseMessage(HttpStatusCode.NotFound);
                case "GET":
                    var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
                    var prefix = query["prefix"] ?? string.Empty;
                    var after = query["continuation-token"];
                    var matching = _objects.Keys.Where(item => item.StartsWith(prefix, StringComparison.Ordinal) && (after is null || string.CompareOrdinal(item, after) > 0)).ToList();
                    var page = matching.Take(pageSize).ToList();
                    var truncated = matching.Count > page.Count;
                    var xml = new StringBuilder("<?xml version=\"1.0\"?><ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\">");
                    xml.Append($"<IsTruncated>{truncated.ToString().ToLowerInvariant()}</IsTruncated>");
                    if (truncated)
                    {
                        xml.Append($"<NextContinuationToken>{page[^1]}</NextContinuationToken>");
                    }

                    foreach (var item in page)
                    {
                        xml.Append($"<Contents><Key>{item}</Key><Size>{_objects[item].Length}</Size><LastModified>2026-10-21T12:00:00.000Z</LastModified></Contents>");
                    }

                    xml.Append("</ListBucketResult>");
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml.ToString(), Encoding.UTF8, "application/xml") };
                default:
                    return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            }
        }
    }
}
