using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;
using MediaEngine.Intelligence;
using MediaEngine.Intelligence.Contracts;
using MediaEngine.Intelligence.Services;
using MediaEngine.Intelligence.Strategies;
using MediaEngine.Providers.Services;

namespace MediaEngine.Api.DependencyInjection;

public static class TuvimaIntelligenceServiceCollectionExtensions
{
    public static IServiceCollection AddTuvimaIntelligence(this IServiceCollection services)
    {
        services.AddSingleton<ExactMatchStrategy>();
        services.AddSingleton<IScoringStrategy>(sp =>
            sp.GetRequiredService<ExactMatchStrategy>());
        services.AddSingleton<IFuzzyMatchingService, FuzzyMatchingService>();
        services.AddSingleton<MediaEngine.Intelligence.Models.ScoringConfiguration>(sp =>
        {
            var settings = sp.GetRequiredService<IConfigurationLoader>().LoadScoring();
            return new MediaEngine.Intelligence.Models.ScoringConfiguration
            {
                AutoLinkThreshold = settings.AutoLinkThreshold,
                ConflictThreshold = settings.ConflictThreshold,
                ConflictEpsilon = settings.ConflictEpsilon,
                StaleClaimDecayDays = settings.StaleClaimDecayDays,
                StaleClaimDecayFactor = settings.StaleClaimDecayFactor,
            };
        });

        services.AddSingleton<IScoringEngine, PriorityCascadeEngine>();
        services.AddSingleton<IRetailMatchScoringService, RetailMatchScoringService>();
        services.AddSingleton<ILocalMatchService, LocalMatchService>();
        foreach (var profile in MediaTypeIdentityProfileCatalog.All)
        {
            services.AddSingleton<IMediaTypeIdentityStrategy>(profile);
        }
        services.AddSingleton<IdentityDecisionService>();
        services.AddSingleton<IIdentityMatcher>(sp =>
            new IdentityMatcher(
                sp.GetRequiredService<IFuzzyMatchingService>(),
                sp.GetRequiredService<ExactMatchStrategy>()));
        services.AddSingleton<ICollectionArbiter>(sp =>
            new CollectionArbiter(
                sp.GetRequiredService<IIdentityMatcher>(),
                sp.GetRequiredService<ITransactionJournal>()));
        services.AddSingleton<IParentCollectionResolver>(sp =>
            new ParentCollectionResolver(
                sp.GetRequiredService<ICollectionRepository>(),
                sp.GetRequiredService<INarrativeRootRepository>(),
                sp.GetRequiredService<ICanonicalValueRepository>(),
                sp.GetRequiredService<ILogger<ParentCollectionResolver>>(),
                sp.GetRequiredService<IQidLabelRepository>(),
                sp.GetRequiredService<IConfigurationLoader>()));
        return services;
    }
}
