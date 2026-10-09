using System.Net.Sockets;
using MediaEngine.Contracts.Startup;

namespace MediaEngine.Api.Tests;

public sealed class ProcessInstanceLeaseTests
{
    [Fact]
    public void Lease_AllowsOnlyOneConcurrentOwner_AndCanBeReacquired()
    {
        var leaseName = $"TuvimaLibrary.Tests.{Guid.NewGuid():N}";

        using (var first = ProcessInstanceLease.TryAcquire(leaseName))
        {
            using (var second = ProcessInstanceLease.TryAcquire(leaseName))
            {
                Assert.True(first.IsAcquired);
                Assert.False(second.IsAcquired);
            }
        }

        using var replacement = ProcessInstanceLease.TryAcquire(leaseName);
        Assert.True(replacement.IsAcquired);
    }

    [Fact]
    public void AddressClassifier_FindsNestedSocketAddressConflict()
    {
        var exception = new IOException(
            "Failed to bind.",
            new InvalidOperationException(
                "Nested.",
                new SocketException((int)SocketError.AddressAlreadyInUse)));

        Assert.True(StartupFailureClassifier.IsAddressAlreadyInUse(exception));
        Assert.False(StartupFailureClassifier.IsAddressAlreadyInUse(new IOException("Different failure.")));
    }

    [Fact]
    public void PathClassifier_ReturnsNestedAccessFailure()
    {
        var denied = new UnauthorizedAccessException("Access to the configured data path is denied.");
        var exception = new InvalidOperationException("Startup failed.", denied);

        Assert.Same(denied, StartupFailureClassifier.FindPathAccessDenied(exception));
        Assert.Null(StartupFailureClassifier.FindPathAccessDenied(new IOException("Different failure.")));
    }

    [Fact]
    public void AcquiredLease_CanBeDisposedFromAnotherThread()
    {
        var leaseName = $"TuvimaLibrary.Tests.{Guid.NewGuid():N}";
        var lease = ProcessInstanceLease.TryAcquire(leaseName);
        Assert.True(lease.IsAcquired);

        Exception? disposalFailure = null;
        var disposalThread = new Thread(() =>
        {
            try
            {
                lease.Dispose();
            }
            catch (Exception exception)
            {
                disposalFailure = exception;
            }
        });
        disposalThread.Start();
        Assert.True(disposalThread.Join(TimeSpan.FromSeconds(5)));

        Assert.Null(disposalFailure);
        using var replacement = ProcessInstanceLease.TryAcquire(leaseName);
        Assert.True(replacement.IsAcquired);
    }
}
