using System.Text;
using Wasmtime;

namespace MediaEngine.Api.Services.Plugins.Sandbox;

/// <summary>What a sandboxed module may ask the Library to do for it. Everything else it asks for does not exist.</summary>
internal interface ISandboxHost
{
    void Log(string message);

    /// <summary>Fetches an address through the plugin's own approved network access. Null when it is refused or fails.</summary>
    byte[]? HttpGet(string url, CancellationToken cancellationToken);
}

/// <summary>A sandboxed run stopped because the module crashed or went over a limit; the message is plain words for the person.</summary>
internal sealed class SandboxRunException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Runs one export of a WebAssembly module with hard limits (Developer tools spec WP21). The module gets no file system, no
/// network, no clock and no programs: the only things it can call are <c>library.log</c> and <c>library.http_get</c>, which
/// go through <see cref="ISandboxHost"/>. A run is stopped when it uses too much memory, too many steps or too much time.
/// </summary>
/// <remarks>
/// The module's contract: it exports <c>memory</c>, <c>alloc(len) -> ptr</c> and the job function
/// <c>(ptr, len) -> i64</c>, which receives the JSON input and returns <c>(result_ptr &lt;&lt; 32) | result_len</c>, or 0 for no
/// result. A fresh engine and instance are made for every run, so nothing survives from one run to the next.
/// </remarks>
internal static class WasmModuleRunner
{
    public const int MaxResultBytes = 1024 * 1024;
    public const int MaxHttpBytes = 1024 * 1024;

    public static string? Run(string modulePath, string export, string inputJson, SandboxDefinition limits, ISandboxHost host, CancellationToken cancellationToken)
    {
        using var config = new Config()
            .WithFuelConsumption(true)
            .WithEpochInterruption(true)
            .WithMaximumStackSize(512 * 1024);
        using var engine = new Engine(config);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timedOut = false;
        using var ticker = timeout.Token.Register(() =>
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            engine.IncrementEpoch();
        });

        try
        {
            using var module = modulePath.EndsWith(".wat", StringComparison.OrdinalIgnoreCase)
                ? Module.FromText(engine, Path.GetFileName(modulePath), File.ReadAllText(modulePath))
                : Module.FromFile(engine, modulePath);
            using var store = new Store(engine);
            store.SetLimits(memorySize: (long)limits.MaxMemoryMb * 1024 * 1024, tableElements: 100_000, instances: 1, tables: 4, memories: 1);
            store.Fuel = (ulong)limits.MaxSteps;
            store.SetEpochDeadline(1);

            // The clock starts once the module is compiled and the limits are set, so the deadline is always ahead of the epoch.
            timeout.CancelAfter(TimeSpan.FromSeconds(limits.MaxSeconds));

            using var linker = new Linker(engine);
            linker.DefineFunction("library", "log", (Caller caller, int ptr, int length) =>
            {
                if (TryRead(caller, ptr, length, 4096) is { } text)
                {
                    host.Log(text);
                }
            });
            linker.DefineFunction("library", "http_get", (Caller caller, int ptr, int length) =>
            {
                if (TryRead(caller, ptr, length, 2048) is not { } url)
                {
                    return 0L;
                }

                var body = host.HttpGet(url, timeout.Token);
                if (body is null || body.Length == 0 || body.Length > MaxHttpBytes)
                {
                    return 0L;
                }

                return Hand(caller, body);
            });

            var instance = linker.Instantiate(store, module);
            var memory = instance.GetMemory("memory") ?? throw new SandboxRunException("The module does not export its memory as \"memory\".");
            var alloc = instance.GetFunction<int, int>("alloc") ?? throw new SandboxRunException("The module does not export \"alloc\" (it takes a length and returns an address).");
            var job = instance.GetFunction<int, int, long>(export) ?? throw new SandboxRunException($"The module does not export a function called \"{export}\" that takes an address and a length and returns a number.");

            var input = Encoding.UTF8.GetBytes(inputJson);
            var inputPtr = alloc(input.Length);
            if (inputPtr < 0 || (long)inputPtr + input.Length > memory.GetLength())
            {
                throw new SandboxRunException("The module's alloc returned an address outside its memory.");
            }

            memory.GetSpan(inputPtr, input.Length).Clear();
            input.CopyTo(memory.GetSpan(inputPtr, input.Length));

            var packed = job(inputPtr, input.Length);
            if (packed == 0)
            {
                return null;
            }

            var resultPtr = (int)(packed >>> 32);
            var resultLength = (int)(packed & 0xFFFFFFFF);
            if (resultLength is <= 0 or > MaxResultBytes || resultPtr < 0 || (long)resultPtr + resultLength > memory.GetLength())
            {
                throw new SandboxRunException("The module returned a result that is empty, too large or outside its memory.");
            }

            return memory.ReadString(resultPtr, resultLength, Encoding.UTF8);
        }
        catch (TrapException ex)
        {
            throw new SandboxRunException(Describe(ex, limits, timedOut), ex);
        }
        catch (WasmtimeException ex)
        {
            throw new SandboxRunException($"The module could not be started: {FirstLine(ex.Message)}", ex);
        }
        catch (OperationCanceledException) when (timedOut)
        {
            throw new SandboxRunException($"The plugin ran for more than {limits.MaxSeconds} seconds and was stopped.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OverflowException)
        {
            throw new SandboxRunException($"The plugin stopped with an error: {FirstLine(ex.Message)}", ex);
        }
    }

    private static string Describe(TrapException ex, SandboxDefinition limits, bool timedOut)
    {
        var message = ex.Message;
        if (timedOut || message.Contains("interrupt", StringComparison.OrdinalIgnoreCase))
        {
            return $"The plugin ran for more than {limits.MaxSeconds} seconds and was stopped.";
        }

        if (message.Contains("fuel", StringComparison.OrdinalIgnoreCase))
        {
            return "The plugin did more work in one run than it is allowed to and was stopped.";
        }

        if (message.Contains("stack", StringComparison.OrdinalIgnoreCase))
        {
            return "The plugin used more stack space than it is allowed to and was stopped.";
        }

        return $"The plugin stopped with an error: {FirstLine(message)}";
    }

    private static string FirstLine(string text) => text.Split('\n', 2)[0].Trim();

    private static string? TryRead(Caller caller, int ptr, int length, int maxLength)
    {
        var memory = caller.GetMemory("memory");
        if (memory is null || ptr < 0 || length < 0 || length > maxLength || (long)ptr + length > memory.GetLength())
        {
            return null;
        }

        return memory.ReadString(ptr, length, Encoding.UTF8);
    }

    // Copies bytes into the module's own memory through its alloc function and returns (address << 32) | length.
    private static long Hand(Caller caller, byte[] bytes)
    {
        var memory = caller.GetMemory("memory");
        var alloc = caller.GetFunction("alloc");
        if (memory is null || alloc is null)
        {
            return 0;
        }

        var pointer = alloc.Invoke(bytes.Length) is int address ? address : 0;
        if (pointer <= 0 || (long)pointer + bytes.Length > memory.GetLength())
        {
            return 0;
        }

        bytes.CopyTo(memory.GetSpan(pointer, bytes.Length));
        return ((long)pointer << 32) | (uint)bytes.Length;
    }
}
