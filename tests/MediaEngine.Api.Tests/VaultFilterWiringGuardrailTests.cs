using System.Reflection;
using MediaEngine.Api.Realtime;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Vault;
using MediaEngine.Domain.Contracts;
using MediaEngine.Ingestion;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The pieces that keep the Vault out of events, logs, operations and ingestion are required by the code that uses them. A constructor
/// or handler parameter of one of these types that defaults to null would let production wiring skip the filter without a single
/// failing test (Videos spec D17, WP17 part C), so none may be optional.
/// </summary>
public sealed class VaultFilterWiringGuardrailTests
{
    private static readonly Type[] FilterTypes =
    [
        typeof(IVaultLibraryRepository), typeof(IVaultIngestionHook), typeof(IVaultPathClassifier), typeof(VaultOperationGate),
        typeof(VaultAccessResolver),
    ];

    [Fact]
    public void NoConstructorOrHandlerLetsAVaultFilterBeLeftOut()
    {
        var assemblies = new[]
        {
            typeof(VaultOperationGate).Assembly, typeof(IngestionEngine).Assembly,
            typeof(VaultLibraryRepository).Assembly, typeof(IVaultLibraryRepository).Assembly,
        };
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var offenders = new List<string>();
        foreach (var type in assemblies.SelectMany(assembly => assembly.GetTypes()))
        {
            foreach (var method in type.GetConstructors(all).Cast<MethodBase>().Concat(type.GetMethods(all)))
            {
                foreach (var parameter in method.GetParameters().Where(parameter => parameter.IsOptional && FilterTypes.Contains(parameter.ParameterType)))
                {
                    offenders.Add($"{type.Name}.{method.Name}({parameter.Name})");
                }
            }
        }

        Assert.True(offenders.Count == 0, "These Vault filters can be skipped by leaving them out: " + string.Join(", ", offenders));
    }
}
