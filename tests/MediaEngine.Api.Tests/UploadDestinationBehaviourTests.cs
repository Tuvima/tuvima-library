using System.Reflection;
using MediaEngine.Api.Endpoints;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Ingestion;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Primitives;
using Options = Microsoft.Extensions.Options.Options;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Runs the real upload handler against hand-built library configurations (including ones a hand-edited file could
/// hold) and checks that only a managed, writable, explicit destination can receive a file.
/// </summary>
public sealed class UploadDestinationBehaviourTests : IDisposable
{
    private const string LibraryId = "11111111-1111-4111-8111-111111111111";
    private const string ChosenId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private const string OtherId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuvima-upload-behaviour", Guid.NewGuid().ToString("N"));

    public UploadDestinationBehaviourTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task DestinationThatIsAsIs_IsRefusedAndNothingIsWritten()
    {
        var asIs = Source(ChosenId, "asis", LibrarySourceManagementModes.ExistingLibrary, LibrarySourceAccessModes.ReadOnly);

        var status = await UploadAsync(Library(destinationId: ChosenId, asIs));

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        AssertNothingWritten();
    }

    [Fact]
    public async Task LibraryWithNoDestination_IsRefusedAndNothingIsWritten()
    {
        var managed = Source(ChosenId, "managed", LibrarySourceManagementModes.ManagedByTuvima, LibrarySourceAccessModes.Writable);

        // A managed folder exists, but the file names no destination: there is no fallback to "the first managed folder".
        var status = await UploadAsync(Library(destinationId: null, managed));

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        AssertNothingWritten();
    }

    [Fact]
    public async Task ManagedDestinationThatIsReadOnly_IsRefusedAndNothingIsWritten()
    {
        // Access is derived from the management mode on save; a hand-edited file can still say read_only.
        var readOnly = Source(ChosenId, "managed", LibrarySourceManagementModes.ManagedByTuvima, LibrarySourceAccessModes.ReadOnly);

        var status = await UploadAsync(Library(destinationId: ChosenId, readOnly));

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        AssertNothingWritten();
    }

    [Fact]
    public async Task DestinationNamedByAnotherManagedFolder_IsNeverReplacedByAFallback()
    {
        var asIs = Source(ChosenId, "asis", LibrarySourceManagementModes.ExistingLibrary, LibrarySourceAccessModes.ReadOnly);
        var managed = Source(OtherId, "managed", LibrarySourceManagementModes.ManagedByTuvima, LibrarySourceAccessModes.Writable);

        var status = await UploadAsync(Library(destinationId: ChosenId, asIs, managed));

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        AssertNothingWritten();
    }

    [Fact]
    public async Task ManagedWritableDestination_ReceivesTheFileInItsOwnFolder()
    {
        var managed = Source(ChosenId, "managed", LibrarySourceManagementModes.ManagedByTuvima, LibrarySourceAccessModes.Writable);
        var engine = EngineProxy.Create();

        var status = await UploadAsync(Library(destinationId: ChosenId, managed), engine);

        Assert.Equal(StatusCodes.Status200OK, status);
        var written = Directory.EnumerateFiles(_root, "*.epub", SearchOption.AllDirectories).Single();
        Assert.StartsWith(Path.Combine(_root, "managed") + Path.DirectorySeparatorChar, written, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ChosenId, ((EngineProxy)(object)engine).Requests.Single().SourceId);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void AssertNothingWritten() =>
        Assert.Empty(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));

    private LibraryFolderConfig Library(string? destinationId, params LibrarySourceConfig[] sources) => new()
    {
        Id = LibraryId,
        Name = "Books",
        Category = "Books",
        Kind = LibraryKinds.Catalogued,
        Area = LibraryAreas.Read,
        MediaTypes = ["Books"],
        AcceptedIntakeModes = [LibraryIntakeModes.BrowserUpload],
        Sources = [.. sources],
        PrimaryDestinationSourceId = destinationId,
    };

    private LibrarySourceConfig Source(string id, string folder, string mode, string access)
    {
        var path = Path.Combine(_root, folder);
        Directory.CreateDirectory(path);
        return new LibrarySourceConfig { Id = id, Path = path, ManagementMode = mode, AccessMode = access };
    }

    private static async Task<int> UploadAsync(LibraryFolderConfig library, IIngestionEngine? engine = null)
    {
        var configuration = DispatchProxy.Create<IConfigurationLoader, LoaderProxy>();
        ((LoaderProxy)(object)configuration).Libraries = new LibrariesConfiguration { Libraries = [library] };

        var bytes = "PK\u0003\u0004 not a real book but non-empty"u8.ToArray();
        var file = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "book.epub");
        var context = new DefaultHttpContext();
        context.Request.Form = new FormCollection(
            new Dictionary<string, StringValues>
            {
                ["mediaType"] = "Books",
                ["destinationLibraryId"] = LibraryId,
            },
            new FormFileCollection { file });

        var result = await IngestionEndpoints.HandleUploadAsync(
            context.Request,
            configuration,
            engine ?? EngineProxy.Create(),
            Options.Create(new IngestionOptions()),
            new SourceMutationPolicyGate(),
            new FixedVaultLibraries(),
            CancellationToken.None);

        return Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? StatusCodes.Status200OK;
    }

    public class LoaderProxy : DispatchProxy
    {
        public LibrariesConfiguration Libraries { get; set; } = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            nameof(IConfigurationLoader.LoadLibraries) => Libraries,
            nameof(IConfigurationLoader.LoadMediaTypes) => new MediaTypeConfiguration(),
            _ => throw new NotSupportedException(targetMethod?.Name),
        };
    }

    public class EngineProxy : DispatchProxy
    {
        public List<IntakeFileRequest> Requests { get; } = [];

        public static IIngestionEngine Create() => DispatchProxy.Create<IIngestionEngine, EngineProxy>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IIngestionEngine.EnqueueIntakeAsync))
            {
                Requests.Add((IntakeFileRequest)args![0]!);
                return Task.CompletedTask;
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }
}
