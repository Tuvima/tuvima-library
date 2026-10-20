using MediaEngine.Api.Services.LocalAssets.Ocr;
using MediaEngine.Domain.Contracts;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace MediaEngine.Api.Tests;

/// <summary>Hand-written stand-in for the helper process. Handlers produce whatever the real helper would write.</summary>
internal sealed class SpyOcrProcessRunner : IOcrProcessRunner
{
    private readonly List<OcrProcessRequest> _requests = [];

    public IReadOnlyList<OcrProcessRequest> Requests => _requests;

    public Func<OcrProcessRequest, OcrProcessResult> Handler { get; set; } =
        _ => new OcrProcessResult(0, "tesseract 5.4.0\n leptonica-1.84.1", string.Empty, false);

    public Task<OcrProcessResult> RunAsync(OcrProcessRequest request, CancellationToken ct)
    {
        _requests.Add(request);
        return Task.FromResult(Handler(request));
    }
}

/// <summary>Stand-in for the whole recognition service so job tests need no helper program.</summary>
internal sealed class FakeOcrService : IOcrService
{
    private int _calls;

    public OcrCapability Capability { get; set; } =
        new(true, "tesseract", "5.4.0", "tessdata", ["eng"], true, null);

    public Func<int, CancellationToken, Task<OcrRecognition>> OnRecognize { get; set; } =
        (call, _) => Task.FromResult(new OcrRecognition($"recognised words number {call}", 91.5, 3));

    public int Calls => _calls;

    public string LanguageDataDirectory { get; } = Path.Combine(Path.GetTempPath(), "tuvima-fake-tessdata");

    public string TemporaryRoot { get; } = Path.Combine(Path.GetTempPath(), $"tuvima-fake-ocr-{Guid.NewGuid():N}");

    /// <summary>When set, asking about the helper throws it, like a failing settings or file read would.</summary>
    public Exception? CapabilityFailure { get; set; }

    public Task<OcrCapability> GetCapabilityAsync(CancellationToken ct = default) =>
        CapabilityFailure is null ? Task.FromResult(Capability) : Task.FromException<OcrCapability>(CapabilityFailure);

    public Task<OcrRecognition> RecognizeAsync(string imagePath, OcrRunOptions options, CancellationToken ct = default)
    {
        Assert.True(File.Exists(imagePath), "The prepared image must exist while it is being read.");
        return OnRecognize(Interlocked.Increment(ref _calls), ct);
    }

    public Task<int?> DetectRotationAsync(string imagePath, OcrRunOptions options, CancellationToken ct = default) =>
        Task.FromResult<int?>(null);

    public void Invalidate()
    {
    }
}

internal sealed class ManualPlaybackProbe : IPlaybackActivityProbe
{
    private readonly object _gate = new();
    private readonly List<(int Target, TaskCompletionSource Signal)> _waiters = [];
    private int _checks;

    public volatile bool Playing;

    public int Checks => Volatile.Read(ref _checks);

    public Task<bool> IsAnyonePlayingAsync(CancellationToken ct = default)
    {
        var checks = Interlocked.Increment(ref _checks);
        lock (_gate)
        {
            foreach (var waiter in _waiters.Where(waiter => waiter.Target <= checks).ToList())
            {
                _waiters.Remove(waiter);
                waiter.Signal.TrySetResult();
            }
        }

        return Task.FromResult(Playing);
    }

    /// <summary>Completes once the worker has asked about playback at least <paramref name="target"/> times.</summary>
    public Task WaitForChecksAsync(int target)
    {
        lock (_gate)
        {
            if (Checks >= target)
            {
                return Task.CompletedTask;
            }

            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((target, signal));
            return signal.Task;
        }
    }
}

internal static class OcrFixtures
{
    /// <summary>A PDF whose pages carry the given text; an empty string makes a scanned-looking blank page.</summary>
    public static byte[] Pdf(params string[] pageTexts)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pageTexts)
        {
            var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
            if (!string.IsNullOrEmpty(text))
            {
                page.AddText(text, 12, new PdfPoint(25, 700), font);
            }
        }

        return builder.Build();
    }

    /// <summary>A plain white picture large enough to be treated as a photo of paper.</summary>
    public static byte[] Png(int width = 600, int height = 400)
    {
        using var bitmap = new SkiaSharp.SKBitmap(width, height);
        bitmap.Erase(SkiaSharp.SKColors.White);
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
