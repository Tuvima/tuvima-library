using MediaEngine.Api.Services.Faces;
using MediaEngine.Domain.Configuration;
using SkiaSharp;

namespace MediaEngine.Api.Tests;

/// <summary>Spec view-people-search WP7: the maths around the two face models, checked without any model.</summary>
public sealed class FaceGeometryTests
{
    [Fact]
    public void LandmarksAlreadyInTheStandardLayout_NeedNoMovement()
    {
        var matrix = FaceGeometry.AlignmentFor(FaceGeometry.MatcherLandmarks);

        Assert.Equal(1f, matrix.ScaleX, 3);
        Assert.Equal(0f, matrix.SkewX, 3);
        Assert.Equal(0f, matrix.TransX, 2);
        Assert.Equal(0f, matrix.TransY, 2);
    }

    [Fact]
    public void ABiggerShiftedFace_IsScaledAndMovedOntoTheStandardLayout()
    {
        var landmarks = FaceGeometry.MatcherLandmarks.Select(point => (point.X * 3 + 200, point.Y * 3 + 80)).ToArray();

        var matrix = FaceGeometry.AlignmentFor(landmarks);

        Assert.Equal(1f / 3f, matrix.ScaleX, 3);
        var mapped = matrix.MapPoint((float)landmarks[2].Item1, (float)landmarks[2].Item2);
        Assert.Equal(FaceGeometry.MatcherLandmarks[2].X, mapped.X, 1);
        Assert.Equal(FaceGeometry.MatcherLandmarks[2].Y, mapped.Y, 1);
    }

    [Fact]
    public void ATurnedFace_IsTurnedBackUpright()
    {
        // The same landmarks rotated a quarter turn about the middle; after alignment the eyes line up again.
        var turned = FaceGeometry.MatcherLandmarks.Select(point => (300 - point.Y, point.X + 20)).ToArray();

        var matrix = FaceGeometry.AlignmentFor(turned);

        for (var index = 0; index < 5; index++)
        {
            var mapped = matrix.MapPoint((float)turned[index].Item1, (float)turned[index].Item2);
            Assert.Equal(FaceGeometry.MatcherLandmarks[index].X, mapped.X, 1);
            Assert.Equal(FaceGeometry.MatcherLandmarks[index].Y, mapped.Y, 1);
        }
    }

    [Fact]
    public void OverlappingBoxes_KeepOnlyTheSurest()
    {
        var faces = new[]
        {
            new FoundFace(100, 100, 80, 80, 0.85, []),
            new FoundFace(104, 102, 80, 80, 0.95, []),
            new FoundFace(400, 300, 80, 80, 0.90, []),
        };

        var kept = FaceGeometry.SuppressOverlaps(faces, 0.3);

        Assert.Equal(2, kept.Count);
        Assert.Equal(0.95, kept[0].Score);
        Assert.Equal(0.90, kept[1].Score);
    }

    [Fact]
    public void FinderOutputs_BecomeAFaceInTheRightPlace()
    {
        var outputs = EmptyOutputs();
        // One cell at stride 16, row 10, column 12: a sure face, centred in its cell, 5 cells wide and 6 tall, nose at the middle.
        var cell = 10 * 40 + 12;
        outputs["cls_16"][cell] = 0.9f;
        outputs["obj_16"][cell] = 0.9f;
        outputs["bbox_16"][cell * 4] = 0.5f;
        outputs["bbox_16"][cell * 4 + 1] = 0.5f;
        outputs["bbox_16"][cell * 4 + 2] = (float)Math.Log(5);
        outputs["bbox_16"][cell * 4 + 3] = (float)Math.Log(6);
        for (var point = 0; point < 5; point++)
        {
            outputs["kps_16"][cell * 10 + point * 2] = 0.5f;
            outputs["kps_16"][cell * 10 + point * 2 + 1] = 0.5f;
        }

        var face = Assert.Single(FaceGeometry.DecodeDetections(outputs, 0.8, 0.3));

        Assert.Equal(0.9, face.Score, 3);
        Assert.Equal((12.5 * 16) - 5 * 16 / 2.0, face.X, 2);
        Assert.Equal((10.5 * 16) - 6 * 16 / 2.0, face.Y, 2);
        Assert.Equal(80, face.Width, 2);
        Assert.Equal(96, face.Height, 2);
        Assert.Equal(12.5 * 16, face.Landmarks[2].X, 2);
    }

    [Fact]
    public void FinderOutputsBelowTheScoreBar_GiveNoFace()
    {
        var outputs = EmptyOutputs();
        outputs["cls_8"][5] = 0.5f;
        outputs["obj_8"][5] = 0.5f;

        Assert.Empty(FaceGeometry.DecodeDetections(outputs, 0.8, 0.3));
    }

    [Fact]
    public void Normalise_GivesAUnitLengthAndLeavesAnEmptyLookAlone()
    {
        var unit = FaceGeometry.Normalise([3f, 4f]);
        Assert.Equal(0.6f, unit[0], 3);
        Assert.Equal(0.8f, unit[1], 3);
        Assert.Equal([0f, 0f], FaceGeometry.Normalise([0f, 0f]));
    }

    [Fact]
    public void ThePinnedRuntimeListCoversEveryPlatformOnGithubWithChecksums()
    {
        var settings = new ToolDownloadSettings();
        settings.RemoveDisallowedEntries();

        Assert.Equal(5, settings.FaceRuntime.Count);
        Assert.All(settings.FaceRuntime, build => Assert.True(ToolDownloadSettings.IsAllowedAddress(build.Url)));
        Assert.NotNull(settings.FindFaceRuntime(ToolDownloadSettings.WindowsX64));
        Assert.NotNull(settings.FindFaceRuntime(ToolDownloadSettings.MacArm64));
        Assert.Null(settings.FindFaceRuntime("freebsd-x64"));
    }

    [Fact]
    public void ARuntimeEntryOnAnotherHost_IsDropped()
    {
        var settings = new ToolDownloadSettings();
        settings.FaceRuntime[0].Url = "https://example.com/onnxruntime.dll";

        settings.RemoveDisallowedEntries();

        Assert.Equal(4, settings.FaceRuntime.Count);
    }

    private static Dictionary<string, float[]> EmptyOutputs()
    {
        var outputs = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var stride in new[] { 8, 16, 32 })
        {
            var cells = (FaceGeometry.DetectorSide / stride) * (FaceGeometry.DetectorSide / stride);
            outputs["cls_" + stride] = new float[cells];
            outputs["obj_" + stride] = new float[cells];
            outputs["bbox_" + stride] = new float[cells * 4];
            outputs["kps_" + stride] = new float[cells * 10];
        }

        return outputs;
    }
}
