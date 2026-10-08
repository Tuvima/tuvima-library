using System.Net.Sockets;
using System.Collections.Concurrent;

namespace MediaEngine.Contracts.Startup;

/// <summary>
/// Prevents two copies of the same Tuvima host from racing for the same local
/// ports. The operating system releases the named mutex when a process exits,
/// including after an abnormal termination.
/// </summary>
public sealed class ProcessInstanceLease : IDisposable
{
    public const string EngineLeaseName = "TuvimaLibrary.Engine";
    public const string DashboardLeaseName = "TuvimaLibrary.Dashboard";

    private static readonly ConcurrentDictionary<string, byte> ProcessLeases = new(StringComparer.Ordinal);

    private readonly MutexLeaseOwner? _owner;
    private readonly string _name;
    private readonly bool _registeredInProcess;
    private int _ownsLease;

    private ProcessInstanceLease(MutexLeaseOwner? owner, string name, bool ownsLease, bool registeredInProcess)
    {
        _owner = owner;
        _name = name;
        _ownsLease = ownsLease ? 1 : 0;
        _registeredInProcess = registeredInProcess;
    }

    public bool IsAcquired => Volatile.Read(ref _ownsLease) == 1;

    public static ProcessInstanceLease TryAcquire(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!ProcessLeases.TryAdd(name, 0))
        {
            return new ProcessInstanceLease(null, name, ownsLease: false, registeredInProcess: false);
        }

        var owner = new MutexLeaseOwner(name);
        try
        {
            var ownsLease = owner.TryAcquire();
            if (!ownsLease)
            {
                ProcessLeases.TryRemove(name, out _);
            }
            return new ProcessInstanceLease(owner, name, ownsLease, registeredInProcess: ownsLease);
        }
        catch
        {
            ProcessLeases.TryRemove(name, out _);
            owner.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _ownsLease, 0) == 1)
        {
            try
            {
                _owner!.Dispose();
            }
            finally
            {
                if (_registeredInProcess)
                {
                    ProcessLeases.TryRemove(_name, out _);
                }
            }
            return;
        }

        _owner?.Dispose();
    }

    private sealed class MutexLeaseOwner : IDisposable
    {
        private readonly Mutex _mutex;
        private readonly ManualResetEventSlim _acquisitionCompleted = new(false);
        private readonly ManualResetEventSlim _releaseRequested = new(false);
        private readonly Thread _ownerThread;
        private Exception? _acquisitionFailure;
        private bool _acquired;
        private int _disposed;

        public MutexLeaseOwner(string name)
        {
            _mutex = new Mutex(initiallyOwned: false, name);
            _ownerThread = new Thread(OwnLease)
            {
                IsBackground = true,
                Name = $"Tuvima lease owner: {name}",
            };
        }

        public bool TryAcquire()
        {
            _ownerThread.Start();
            _acquisitionCompleted.Wait();
            if (_acquisitionFailure is not null)
            {
                global::System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(_acquisitionFailure).Throw();
            }
            return _acquired;
        }

        private void OwnLease()
        {
            try
            {
                try
                {
                    _acquired = _mutex.WaitOne(0);
                }
                catch (AbandonedMutexException)
                {
                    // The previous process exited without cleanup. Windows gives this
                    // owner the abandoned mutex, so the new host can start safely.
                    _acquired = true;
                }
            }
            catch (Exception exception)
            {
                _acquisitionFailure = exception;
            }
            finally
            {
                _acquisitionCompleted.Set();
            }

            if (!_acquired)
            {
                return;
            }

            _releaseRequested.Wait();
            _mutex.ReleaseMutex();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            if (_acquired)
            {
                _releaseRequested.Set();
            }
            if (_ownerThread.IsAlive)
            {
                _ownerThread.Join();
            }
            _releaseRequested.Dispose();
            _acquisitionCompleted.Dispose();
            _mutex.Dispose();
        }
    }
}

public static class StartupFailureClassifier
{
    public static bool IsAddressAlreadyInUse(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
            {
                return true;
            }
        }

        return false;
    }

    public static UnauthorizedAccessException? FindPathAccessDenied(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is UnauthorizedAccessException denied)
            {
                return denied;
            }
        }

        return null;
    }
}
