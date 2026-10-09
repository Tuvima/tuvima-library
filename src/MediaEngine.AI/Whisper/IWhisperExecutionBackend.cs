using MediaEngine.AI.Configuration;
using MediaEngine.AI.Infrastructure;
using MediaEngine.Domain.Enums;
using Whisper.net;

namespace MediaEngine.AI.Whisper;

internal interface IWhisperExecutionBackend : IAsyncDisposable
{
    Task<IReadOnlyList<TranscriptionSegment>> TranscribeAsync(
        string modelPath,
        string language,
        bool translate,
        string wavFilePath,
        CancellationToken cancellationToken);

    Task<(string LanguageCode, double Confidence)> DetectLanguageAsync(
        string modelPath,
        string wavFilePath,
        CancellationToken cancellationToken);

    ValueTask DisposeModelAsync(AiModelRole role, CancellationToken cancellationToken);
}

internal sealed class WhisperExecutionBackend(AiSettings settings) : IWhisperExecutionBackend
{
    private readonly SemaphoreSlim _factoryGate = new(1, 1);
    private WhisperFactory? _factory;
    private string? _modelPath;
    private int _disposed;

    public async Task<IReadOnlyList<TranscriptionSegment>> TranscribeAsync(
        string modelPath,
        string language,
        bool translate,
        string wavFilePath,
        CancellationToken cancellationToken)
    {
        var factory = await GetOrLoadFactoryAsync(modelPath, cancellationToken).ConfigureAwait(false);
        var builder = factory.CreateBuilder().WithLanguage(language);
        if (translate)
        {
            builder = builder.WithTranslate();
        }

        using var processor = builder.Build();
        await using var wavStream = File.OpenRead(wavFilePath);
        var segments = new List<TranscriptionSegment>();
        await foreach (var segment in processor
            .ProcessAsync(wavStream, cancellationToken)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            segments.Add(new TranscriptionSegment
            {
                StartMs = (long)segment.Start.TotalMilliseconds,
                EndMs = (long)segment.End.TotalMilliseconds,
                Text = segment.Text.Trim(),
                Confidence = segment.Probability,
            });
        }

        return segments;
    }

    public async Task<(string LanguageCode, double Confidence)> DetectLanguageAsync(
        string modelPath,
        string wavFilePath,
        CancellationToken cancellationToken)
    {
        var factory = await GetOrLoadFactoryAsync(modelPath, cancellationToken).ConfigureAwait(false);
        using var processor = factory.CreateBuilder()
            .WithLanguageDetection()
            .Build();
        await using var wavStream = File.OpenRead(wavFilePath);

        await foreach (var segment in processor
            .ProcessAsync(wavStream, cancellationToken)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            if (!string.IsNullOrWhiteSpace(segment.Language))
            {
                return (segment.Language, segment.Probability);
            }

            break;
        }

        return ("en", 0.0);
    }

    public async ValueTask DisposeModelAsync(
        AiModelRole role,
        CancellationToken cancellationToken)
    {
        if (role != AiModelRole.Audio)
        {
            return;
        }

        await _factoryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _factory?.Dispose();
            _factory = null;
            _modelPath = null;
        }
        finally
        {
            _factoryGate.Release();
        }
    }

    private async Task<WhisperFactory> GetOrLoadFactoryAsync(
        string modelPath,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _factoryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_factory is not null
                && string.Equals(_modelPath, modelPath, StringComparison.OrdinalIgnoreCase))
            {
                return _factory;
            }

            _factory?.Dispose();
            new SharedAiRuntime(settings, Microsoft.Extensions.Logging.Abstractions.NullLogger<SharedAiRuntime>.Instance).EnsureWhisper();
            _factory = WhisperFactory.FromPath(modelPath);
            _modelPath = modelPath;
            return _factory;
        }
        finally
        {
            _factoryGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _factoryGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _factory?.Dispose();
            _factory = null;
            _modelPath = null;
        }
        finally
        {
            _factoryGate.Release();
            _factoryGate.Dispose();
        }
    }
}
