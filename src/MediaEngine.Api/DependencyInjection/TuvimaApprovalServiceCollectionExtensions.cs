using MediaEngine.Api.Services.Approvals;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MediaEngine.Api.DependencyInjection;

/// <summary>The approval rule (Developer tools spec D-C): the words shown before something is switched on, and the record of who confirmed.</summary>
public static class TuvimaApprovalServiceCollectionExtensions
{
    public static IServiceCollection AddTuvimaApprovals(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ApprovalService>();
        return services;
    }
}
