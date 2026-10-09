using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using MediaEngine.Contracts.Details;

namespace MediaEngine.Contracts.Tests;

public sealed class ContractShapeTests
{
    [Fact]
    public void PublicContractShape_MatchesApprovedFixture()
    {
        var actual = BuildContractShape();
        SnapshotFixture.Verify("contracts-shape.approved.txt", actual, "TUVIMA_UPDATE_CONTRACT_SHAPE");
    }

    private static string BuildContractShape()
    {
        var nullability = new NullabilityInfoContext();
        var builder = new StringBuilder();
        var assembly = typeof(DetailPageViewModel).Assembly;
        var contractTypes = assembly
            .GetExportedTypes()
            .Where(type => type.Namespace is not null
                && type.Namespace.StartsWith("MediaEngine.Contracts.", StringComparison.Ordinal)
                && type is { IsClass: true } or { IsValueType: true }
                && !type.IsSpecialName)
            .OrderBy(type => type.Namespace, StringComparer.Ordinal)
            .ThenBy(type => GetFriendlyTypeName(type), StringComparer.Ordinal);

        foreach (var type in contractTypes)
        {
            builder.AppendLine($"{type.Namespace}.{GetFriendlyTypeName(type)}");

            var properties = type
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(property => property.GetMethod?.IsPublic == true || property.SetMethod?.IsPublic == true)
                .OrderBy(property => property.MetadataToken);

            foreach (var property in properties)
            {
                var nullabilityState = GetNullabilityState(nullability, property);
                var accessors = GetAccessorShape(property);
                builder.AppendLine($"  {property.Name}: {GetFriendlyTypeName(property.PropertyType)} | nullability={nullabilityState} | accessors={accessors}");
            }

            builder.AppendLine();
        }

        return builder.ToString().ReplaceLineEndings("\n");
    }

    private static string GetNullabilityState(NullabilityInfoContext nullability, PropertyInfo property)
    {
        if (property.PropertyType.IsValueType)
        {
            return Nullable.GetUnderlyingType(property.PropertyType) is null ? "not-null" : "nullable";
        }

        var info = nullability.Create(property);
        return info.ReadState switch
        {
            NullabilityState.NotNull => "not-null",
            NullabilityState.Nullable => "nullable",
            _ => "unknown",
        };
    }

    private static string GetAccessorShape(PropertyInfo property)
    {
        var parts = new List<string>();
        if (property.GetMethod?.IsPublic == true)
        {
            parts.Add("get");
        }

        if (property.SetMethod?.IsPublic == true)
        {
            parts.Add(IsInitOnly(property.SetMethod) ? "init" : "set");
        }

        return string.Join(",", parts);
    }

    private static bool IsInitOnly(MethodInfo setMethod)
    {
        var modifiers = setMethod.ReturnParameter.GetRequiredCustomModifiers();
        return modifiers.Contains(typeof(IsExternalInit));
    }

    private static string GetFriendlyTypeName(Type type)
    {
        if (type.IsGenericParameter)
        {
            return type.Name;
        }

        var nullableInner = Nullable.GetUnderlyingType(type);
        if (nullableInner is not null)
        {
            return $"{GetFriendlyTypeName(nullableInner)}?";
        }

        if (type.IsArray)
        {
            return $"{GetFriendlyTypeName(type.GetElementType()!)}[]";
        }

        if (!type.IsGenericType)
        {
            return type.FullName ?? type.Name;
        }

        var typeDefinitionName = type.GetGenericTypeDefinition().FullName ?? type.Name;
        var tickIndex = typeDefinitionName.IndexOf('`', StringComparison.Ordinal);
        if (tickIndex >= 0)
        {
            typeDefinitionName = typeDefinitionName[..tickIndex];
        }

        var arguments = string.Join(", ", type.GetGenericArguments().Select(GetFriendlyTypeName));
        return $"{typeDefinitionName}<{arguments}>";
    }

}
