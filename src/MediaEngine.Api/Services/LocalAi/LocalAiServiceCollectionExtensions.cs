using MediaEngine.AI.EngineRoom;
using MediaEngine.AI.Llama;

namespace MediaEngine.Api.Services.LocalAi;

/// <summary>
/// Registers the Local AI engine room and the one text inference service behind it. This is the only place that wires
/// the inference service to anything: every other caller asks the engine room for an ability.
/// </summary>
public static class LocalAiServiceCollectionExtensions
{
    public static IServiceCollection AddTuvimaLocalAiEngineRoom(this IServiceCollection services)
    {
        services.AddSingleton<LlamaInferenceService>();
        services.AddSingleton<ITextInferenceService>(sp => sp.GetRequiredService<LlamaInferenceService>());
        services.AddSingleton<AiAdmissionRules>();
        services.AddSingleton<IAiAdmissionRules>(sp => sp.GetRequiredService<AiAdmissionRules>());
        services.AddSingleton<PluginAiBudget>();
        services.AddSingleton<LocalAiEngineRoom>();
        services.AddSingleton<IAiEngineRoom>(sp => sp.GetRequiredService<LocalAiEngineRoom>());
        return services;
    }
}
