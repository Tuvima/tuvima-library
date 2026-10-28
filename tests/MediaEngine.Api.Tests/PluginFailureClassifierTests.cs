using System.ComponentModel;
using System.Net;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Domain.Plugins;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Tests;

/// <summary>Every failure raised inside a plugin lands on one cause from the fixed list (Plugin architecture spec C17).</summary>
public sealed class PluginFailureClassifierTests
{
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, PluginFailureReason.KeyRejected)]
    [InlineData(HttpStatusCode.Forbidden, PluginFailureReason.KeyRejected)]
    [InlineData(HttpStatusCode.TooManyRequests, PluginFailureReason.ServiceRateLimited)]
    [InlineData(HttpStatusCode.ServiceUnavailable, PluginFailureReason.ServiceUnreachable)]
    [InlineData(HttpStatusCode.NotFound, PluginFailureReason.ServiceRefused)]
    public void AnHttpAnswer_IsTheOutsideService(HttpStatusCode status, PluginFailureReason expected) =>
        Assert.Equal(expected, PluginFailureClassifier.Classify(new HttpRequestException("x", null, status)));

    [Fact]
    public void ANetworkFailureOrTimeout_IsTheOutsideServiceBeingUnreachable()
    {
        Assert.Equal(PluginFailureReason.ServiceUnreachable, PluginFailureClassifier.Classify(new HttpRequestException("no route")));
        Assert.Equal(PluginFailureReason.ServiceUnreachable, PluginFailureClassifier.Classify(new TaskCanceledException()));
        Assert.Equal(PluginFailureReason.ServiceUnreachable, PluginFailureClassifier.Classify(new TimeoutException()));
    }

    [Fact]
    public void AProgramThatCouldNotStart_IsAMissingTool_ButAMissingMediaFileIsTheItem()
    {
        Assert.Equal(PluginFailureReason.ToolMissing, PluginFailureClassifier.Classify(new Win32Exception("The system cannot find the file specified")));
        Assert.Equal(PluginFailureReason.ToolMissing, PluginFailureClassifier.Classify(new FileNotFoundException("missing", "ffmpeg")));
        Assert.Equal(PluginFailureReason.ItemUnreadable, PluginFailureClassifier.Classify(new FileNotFoundException("missing", "movie.mkv")));
        Assert.Equal(PluginFailureReason.ItemUnreadable, PluginFailureClassifier.Classify(new IOException("locked")));
    }

    [Fact]
    public void ADiskThatIsFull_IsTheLibraryNotThePlugin() =>
        Assert.Equal(PluginFailureReason.LibraryDisk, PluginFailureClassifier.Classify(new IOException("No space left on device")));

    [Fact]
    public void OtherKinds_AreClassifiedByWhatTheyAre()
    {
        Assert.Equal(PluginFailureReason.LimitRequests, PluginFailureClassifier.Classify(new PluginLimitExceededException("too many")));
        Assert.Equal(PluginFailureReason.ItemUnsupported, PluginFailureClassifier.Classify(new NotSupportedException()));
        Assert.Equal(PluginFailureReason.NotAllowedPermission, PluginFailureClassifier.Classify(new UnauthorizedAccessException()));
        Assert.Equal(PluginFailureReason.LibraryDataStore, PluginFailureClassifier.Classify(new Microsoft.Data.Sqlite.SqliteException("locked", 5)));
        Assert.Equal(PluginFailureReason.PluginCrashed, PluginFailureClassifier.Classify(new NullReferenceException()));
    }
}
