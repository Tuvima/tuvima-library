using MediaEngine.Api.Services.Security;

namespace MediaEngine.Api.DependencyInjection;

public static class TuvimaSecurityServiceCollectionExtensions
{
    public static IServiceCollection AddTuvimaSecurity(this IServiceCollection services)
    {
        services.AddScoped<IUsableAdministratorService, UsableAdministratorService>();
        return services;
    }
}
