using MediaEngine.Api.Services.Tools;
using MediaEngine.Domain.Contracts;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MediaEngine.Api.DependencyInjection;

public static class TuvimaToolRequirementsServiceCollectionExtensions
{
    public static IServiceCollection AddTuvimaToolRequirements(this IServiceCollection services)
    {
        services.AddSingleton<IToolRuntimeProbe, ToolRuntimeProbe>();
        services.AddSingleton<IToolRequirementsService, ToolRequirementsService>();
        services.AddSingleton<ToolLocationService>();
        // Nothing is held back until the work that holds files registers its own counter in place of this one.
        services.TryAddSingleton<IToolWaitingWorkCounter, NoToolWaitingWorkCounter>();
        services.AddHostedService<ToolRequirementsStartupService>();
        return services;
    }
}
