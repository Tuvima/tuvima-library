using SkiaSharp;

namespace MediaEngine.Api.Services.Faces;

/// <summary>One face as the finder reports it, in pixels of the upright picture: its box, how sure the finder is, and the five landmarks (right eye, left eye, nose, right and left mouth corner as seen in the picture).</summary>
public sealed record FoundFace(double X, double Y, double Width, double Height, double Score, (double X, double Y)[] Landmarks);

/// <summary>
/// The maths around the two face models, kept apart from the model runner so it can be tested without any model (spec view-people-search WP7):
/// turning the finder's raw numbers into faces, dropping overlaps, and straightening a face to the size the matcher expects.
/// </summary>
public static class FaceGeometry
{
    /// <summary>The side of the square picture the finder looks at.</summary>
    public const int DetectorSide = 640;

    /// <summary>The side of the straightened face the matcher looks at.</summary>
    public const int MatcherSide = 112;

    /// <summary>Where the five landmarks sit in the straightened 112 by 112 face (the layout SFace was trained with).</summary>
    public static readonly (double X, double Y)[] MatcherLandmarks =
    [
        (38.2946, 51.6963), (73.5318, 51.5014), (56.0252, 71.7366), (41.5493, 92.3655), (70.7299, 92.2041),
    ];

    private static readonly int[] Strides = [8, 16, 32];

    /// <summary>
    /// Reads the finder's twelve outputs (cls, obj, bbox and kps for strides 8, 16 and 32) into faces in the finder's 640 square, then drops
    /// overlapping boxes. <paramref name="outputs"/> is keyed by the output names the model uses (for example <c>cls_8</c>).
    /// </summary>
    public static IReadOnlyList<FoundFace> DecodeDetections(IReadOnlyDictionary<string, float[]> outputs, double minScore, double maxOverlap)
    {
        var found = new List<FoundFace>();
        foreach (var stride in Strides)
        {
            var cls = outputs["cls_" + stride];
            var obj = outputs["obj_" + stride];
            var bbox = outputs["bbox_" + stride];
            var kps = outputs["kps_" + stride];
            var columns = DetectorSide / stride;
            var cells = columns * columns;
            for (var index = 0; index < cells; index++)
            {
                var score = Math.Sqrt(Math.Clamp(cls[index], 0f, 1f) * Math.Clamp(obj[index], 0f, 1f));
                if (score < minScore)
                {
                    continue;
                }

                var row = index / columns;
                var column = index % columns;
                var centreX = (column + bbox[index * 4]) * stride;
                var centreY = (row + bbox[index * 4 + 1]) * stride;
                var width = Math.Exp(bbox[index * 4 + 2]) * stride;
                var height = Math.Exp(bbox[index * 4 + 3]) * stride;
                var landmarks = new (double X, double Y)[5];
                for (var point = 0; point < 5; point++)
                {
                    landmarks[point] = ((kps[index * 10 + point * 2] + column) * stride, (kps[index * 10 + point * 2 + 1] + row) * stride);
                }

                found.Add(new FoundFace(centreX - width / 2, centreY - height / 2, width, height, score, landmarks));
            }
        }

        return SuppressOverlaps(found, maxOverlap);
    }

    /// <summary>Keeps the surest face of each overlapping group: a box that overlaps a surer one by more than <paramref name="maxOverlap"/> is dropped.</summary>
    public static IReadOnlyList<FoundFace> SuppressOverlaps(IEnumerable<FoundFace> faces, double maxOverlap)
    {
        var kept = new List<FoundFace>();
        foreach (var face in faces.OrderByDescending(item => item.Score))
        {
            if (kept.All(other => OverlapRatio(face, other) <= maxOverlap))
            {
                kept.Add(face);
            }
        }

        return kept;
    }

    /// <summary>Intersection over union of two boxes: 0 when apart, 1 when identical.</summary>
    public static double OverlapRatio(FoundFace first, FoundFace second)
    {
        var left = Math.Max(first.X, second.X);
        var top = Math.Max(first.Y, second.Y);
        var right = Math.Min(first.X + first.Width, second.X + second.Width);
        var bottom = Math.Min(first.Y + first.Height, second.Y + second.Height);
        var intersection = Math.Max(0, right - left) * Math.Max(0, bottom - top);
        var union = first.Width * first.Height + second.Width * second.Height - intersection;
        return union <= 0 ? 0 : intersection / union;
    }

    /// <summary>
    /// The turn, scale and shift that carry the face's landmarks onto the matcher's standard layout (a similarity fit: faces keep their shape).
    /// </summary>
    public static SKMatrix AlignmentFor(IReadOnlyList<(double X, double Y)> landmarks)
    {
        var count = landmarks.Count;
        double meanSourceX = 0, meanSourceY = 0, meanTargetX = 0, meanTargetY = 0;
        for (var index = 0; index < count; index++)
        {
            meanSourceX += landmarks[index].X;
            meanSourceY += landmarks[index].Y;
            meanTargetX += MatcherLandmarks[index].X;
            meanTargetY += MatcherLandmarks[index].Y;
        }

        meanSourceX /= count;
        meanSourceY /= count;
        meanTargetX /= count;
        meanTargetY /= count;

        // Points as complex numbers: target = a * source + b, with a carrying both the turn and the scale.
        double numeratorReal = 0, numeratorImaginary = 0, denominator = 0;
        for (var index = 0; index < count; index++)
        {
            var sourceX = landmarks[index].X - meanSourceX;
            var sourceY = landmarks[index].Y - meanSourceY;
            var targetX = MatcherLandmarks[index].X - meanTargetX;
            var targetY = MatcherLandmarks[index].Y - meanTargetY;
            numeratorReal += sourceX * targetX + sourceY * targetY;
            numeratorImaginary += sourceX * targetY - sourceY * targetX;
            denominator += sourceX * sourceX + sourceY * sourceY;
        }

        if (denominator <= 0)
        {
            return SKMatrix.Identity;
        }

        var real = numeratorReal / denominator;
        var imaginary = numeratorImaginary / denominator;
        var shiftX = meanTargetX - (real * meanSourceX - imaginary * meanSourceY);
        var shiftY = meanTargetY - (imaginary * meanSourceX + real * meanSourceY);
        return new SKMatrix
        {
            ScaleX = (float)real,
            SkewX = (float)-imaginary,
            TransX = (float)shiftX,
            SkewY = (float)imaginary,
            ScaleY = (float)real,
            TransY = (float)shiftY,
            Persp2 = 1,
        };
    }

    /// <summary>Scales the numbers so their squared sum is 1; a face and its look-alike then compare by a plain dot product.</summary>
    public static float[] Normalise(float[] values)
    {
        double sum = 0;
        foreach (var value in values)
        {
            sum += value * value;
        }

        var length = Math.Sqrt(sum);
        if (length <= 1e-9)
        {
            return values;
        }

        var result = new float[values.Length];
        for (var index = 0; index < values.Length; index++)
        {
            result[index] = (float)(values[index] / length);
        }

        return result;
    }
}
