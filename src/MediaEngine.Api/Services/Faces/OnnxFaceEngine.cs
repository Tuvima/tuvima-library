using System.Runtime.InteropServices;
using MediaEngine.Domain.PersonalMedia;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace MediaEngine.Api.Services.Faces;

/// <summary>
/// The real face engine (spec view-people-search WP7, D13): YuNet finds faces and five landmarks, SFace turns each straightened face into a
/// 128-number fingerprint, and ONNX Runtime runs both on the person's own computer. Nothing is sent anywhere. The native runtime library and
/// the two model files are fetched once through Tools and checked against pinned checksums (<see cref="FaceModelProvider"/>); until all three are
/// in place <see cref="IsAvailable"/> is false and People says it is getting the face tools.
/// </summary>
public sealed class OnnxFaceEngine(FaceModelProvider provider, ILogger<OnnxFaceEngine> logger) : IFaceEngine, IDisposable
{
    /// <summary>A face the finder is less sure of than this is not kept.</summary>
    public const double MinScore = 0.8;

    /// <summary>Two boxes overlapping more than this are the same face; the surer one stays.</summary>
    public const double MaxOverlap = 0.3;

    /// <summary>Faces narrower than this many pixels of the upright picture are skipped: the matcher cannot tell tiny faces apart.</summary>
    public const int MinFacePixels = 32;

    private static readonly object ResolverGate = new();
    private static bool _resolverSet;
    private static string? _runtimePath;

    private readonly object _gate = new();
    private InferenceSession? _detector;
    private InferenceSession? _matcher;
    private DateTime _nextTry = DateTime.MinValue;

    public string ModelId => "yunet-2023mar+sface-2021dec";

    public bool IsAvailable
    {
        get
        {
            if (_detector is not null && _matcher is not null)
            {
                return true;
            }

            lock (_gate)
            {
                if (_detector is not null && _matcher is not null)
                {
                    return true;
                }

                if (DateTime.UtcNow < _nextTry)
                {
                    return false;
                }

                // Hashing the files and loading the models is slow; a failed or not-yet-ready attempt waits before the next one.
                _nextTry = DateTime.UtcNow.AddSeconds(30);
                if (!provider.AreReady || provider.RuntimePath is not { } runtime)
                {
                    return false;
                }

                try
                {
                    UseRuntime(runtime);
                    var options = new Microsoft.ML.OnnxRuntime.SessionOptions { IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 4), LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR };
                    _detector = new InferenceSession(provider.PathOf(MediaEngine.Domain.Configuration.FaceModelFiles.Detection), options);
                    _matcher = new InferenceSession(provider.PathOf(MediaEngine.Domain.Configuration.FaceModelFiles.Matching), options);
                    logger.LogInformation("The face tools are loaded.");
                    return true;
                }
                catch (Exception ex) when (ex is OnnxRuntimeException or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or TypeInitializationException or InvalidOperationException)
                {
                    // Not fatal: People keeps saying it is getting the face tools, and the next check tries again a little later.
                    logger.LogWarning(ex, "The face tools could not be loaded.");
                    _nextTry = DateTime.UtcNow.AddMinutes(10);
                    _detector?.Dispose();
                    _detector = null;
                    _matcher?.Dispose();
                    _matcher = null;
                    return false;
                }
            }
        }
    }

    public Task<IReadOnlyList<ViewDetectedFace>> AnalyseAsync(string imagePath, CancellationToken ct)
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException("The face tools are not in place yet.");
        }

        return Task.Run(() => Analyse(imagePath, ct), ct);
    }

    public void Dispose()
    {
        _detector?.Dispose();
        _matcher?.Dispose();
    }

    private IReadOnlyList<ViewDetectedFace> Analyse(string imagePath, CancellationToken ct)
    {
        using var picture = OrientedImage.Load(imagePath)
            ?? throw new InvalidDataException("The photo could not be read.");
        ct.ThrowIfCancellationRequested();

        var found = FindFaces(picture);
        var result = new List<ViewDetectedFace>(found.Count);
        foreach (var face in found)
        {
            ct.ThrowIfCancellationRequested();
            var embedding = Describe(picture, face);
            result.Add(new ViewDetectedFace(face.X / picture.Width, face.Y / picture.Height, face.Width / picture.Width, face.Height / picture.Height, face.Score, embedding));
        }

        return result;
    }

    private List<FoundFace> FindFaces(SKBitmap picture)
    {
        var side = FaceGeometry.DetectorSide;
        var scale = Math.Min((double)side / picture.Width, (double)side / picture.Height);
        var input = new DenseTensor<float>([1, 3, side, side]);
        using (var square = new SKBitmap(side, side, SKColorType.Rgba8888, SKAlphaType.Premul))
        {
            using (var canvas = new SKCanvas(square))
            {
                canvas.Clear(SKColors.Black);
                using var image = SKImage.FromBitmap(picture);
                canvas.DrawImage(image, new SKRect(0, 0, (float)(picture.Width * scale), (float)(picture.Height * scale)), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            }

            FillBlueGreenRed(square.GetPixelSpan(), input.Buffer.Span, side, side);
        }

        IReadOnlyList<FoundFace> raw;
        using (var outputs = _detector!.Run([NamedOnnxValue.CreateFromTensor(_detector.InputNames[0], input)]))
        {
            var byName = outputs.ToDictionary(output => output.Name, output => output.AsTensor<float>().ToArray(), StringComparer.OrdinalIgnoreCase);
            raw = FaceGeometry.DecodeDetections(byName, MinScore, MaxOverlap);
        }

        var faces = new List<FoundFace>(raw.Count);
        foreach (var face in raw)
        {
            // Back from the finder's square to the picture, kept inside it.
            var left = Math.Max(0, face.X / scale);
            var top = Math.Max(0, face.Y / scale);
            var right = Math.Min(picture.Width, (face.X + face.Width) / scale);
            var bottom = Math.Min(picture.Height, (face.Y + face.Height) / scale);
            if (right - left < MinFacePixels || bottom - top < MinFacePixels)
            {
                continue;
            }

            faces.Add(new FoundFace(left, top, right - left, bottom - top, face.Score, face.Landmarks.Select(point => (point.X / scale, point.Y / scale)).ToArray()));
        }

        return faces;
    }

    private float[] Describe(SKBitmap picture, FoundFace face)
    {
        var side = FaceGeometry.MatcherSide;
        var input = new DenseTensor<float>([1, 3, side, side]);
        using (var straightened = new SKBitmap(side, side, SKColorType.Rgba8888, SKAlphaType.Premul))
        {
            using (var canvas = new SKCanvas(straightened))
            {
                canvas.Clear(SKColors.Black);
                canvas.SetMatrix(FaceGeometry.AlignmentFor(face.Landmarks));
                using var image = SKImage.FromBitmap(picture);
                canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            }

            FillBlueGreenRed(straightened.GetPixelSpan(), input.Buffer.Span, side, side);
        }

        using var outputs = _matcher!.Run([NamedOnnxValue.CreateFromTensor(_matcher.InputNames[0], input)]);
        return FaceGeometry.Normalise(outputs.First().AsTensor<float>().ToArray());
    }

    // Both models expect blue, green, red planes with values 0 to 255 (the order OpenCV uses), not the red, green, blue a photo is stored in.
    private static void FillBlueGreenRed(ReadOnlySpan<byte> rgba, Span<float> planes, int width, int height)
    {
        var plane = width * height;
        for (var index = 0; index < plane; index++)
        {
            planes[index] = rgba[index * 4 + 2];
            planes[plane + index] = rgba[index * 4 + 1];
            planes[plane * 2 + index] = rgba[index * 4];
        }
    }

    /// <summary>Points ONNX Runtime's own library name at the file Tools fetched. Allowed once per process, so later calls only check the path is the same.</summary>
    private static void UseRuntime(string path)
    {
        lock (ResolverGate)
        {
            _runtimePath = path;
            if (_resolverSet)
            {
                return;
            }

            NativeLibrary.SetDllImportResolver(typeof(OrtEnv).Assembly, (name, _, _) =>
                name == "onnxruntime" && _runtimePath is { } file && File.Exists(file) ? NativeLibrary.Load(file) : IntPtr.Zero);
            _resolverSet = true;
        }
    }
}
