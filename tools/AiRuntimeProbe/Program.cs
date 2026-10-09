using System.Diagnostics;
using LLama;
using LLama.Common;
using MediaEngine.AI.Configuration;
using MediaEngine.AI.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

var settings = new AiSettings
{
    NativeRuntimeDirectory = Environment.GetEnvironmentVariable("TUVIMA_AI_RUNTIME_DIR") ?? "",
    ModelsDirectory = Environment.GetEnvironmentVariable("TUVIMA_MODELS_DIR") ?? throw new InvalidOperationException("Set TUVIMA_MODELS_DIR."),
};
var runtime = new SharedAiRuntime(settings, NullLogger<SharedAiRuntime>.Instance);
if (args.Contains("--whisper"))
{
    var path = runtime.EnsureWhisper();
    var info = Whisper.net.WhisperFactory.GetRuntimeInfo();
    if (string.IsNullOrWhiteSpace(info))
    {
        throw new InvalidOperationException("Whisper returned no native runtime information.");
    }
    var modules = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
        .Where(module => module.ModuleName.Equals("whisper.dll", StringComparison.OrdinalIgnoreCase)
            || module.ModuleName.Contains("ggml", StringComparison.OrdinalIgnoreCase)).ToArray();
    foreach (var module in modules)
    {
        Console.WriteLine($"Loaded module: {module.FileName}");
        if (settings.NativeRuntimeDirectory != "bundled" && !module.FileName.StartsWith(runtime.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Whisper module loaded outside the configured runtime installation.");
        }
    }
    Console.WriteLine($"Whisper wrapper/native load succeeded: {path}\n{info}");
    return;
}
var selectedPath = runtime.EnsureLlama(args.Contains("--cpu") ? false : null);
if (args.Contains("--cuda") && runtime.LoadedBackend != "cuda")
{
    throw new InvalidOperationException("CUDA validation selected CPU fallback.");
}
var inventory = new ModelInventory(settings, NullLogger<ModelInventory>.Instance);
var model = inventory.GetModelPath(MediaEngine.Domain.Enums.AiModelRole.TextQuality);
using var lease = SharedModelArtifact.AcquireRead(model);
var parameters = new ModelParams(model)
{
    ContextSize = 512,
    GpuLayerCount = runtime.LoadedBackend == "cuda" ? 999 : 0,
    Threads = 4,
    BatchThreads = 4,
};
using var weights = LLamaWeights.LoadFromFile(parameters);
var executor = new StatelessExecutor(weights, parameters) { ApplyTemplate = true };
var response = "";
await foreach (var token in executor.InferAsync("Reply with the word ready. /no_think", new InferenceParams { MaxTokens = 32 }))
{
    response += token;
}
if (string.IsNullOrWhiteSpace(response))
{
    throw new InvalidOperationException("Inference returned no output.");
}
Console.WriteLine($"Backend: {runtime.LoadedBackend}\nLibrary: {selectedPath}\nModel: {model}\nResponse: {response}");
foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
{
    if (module.ModuleName.Contains("ggml", StringComparison.OrdinalIgnoreCase)
        || module.ModuleName.Contains("cublas", StringComparison.OrdinalIgnoreCase)
        || module.ModuleName.Contains("cudart", StringComparison.OrdinalIgnoreCase)
        || module.ModuleName.Equals("llama.dll", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"Loaded module: {module.FileName}");
        if (settings.NativeRuntimeDirectory != "bundled" && !module.FileName.StartsWith(runtime.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("AI module loaded outside the configured runtime installation.");
        }
    }
}
