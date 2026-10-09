using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using MediaEngine.Api.Models;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.System;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Contracts.Tests;

/// <summary>
/// Freezes the JSON boundary before API- and Dashboard-local DTOs move into
/// MediaEngine.Contracts. The snapshot deliberately records wire behavior, not
/// only CLR names, so a namespace move cannot accidentally rename, omit, reorder,
/// or change the mutability/nullability of a field.
/// </summary>
public sealed class WireContractSnapshotTests
{
    private static readonly JsonSerializerOptions WebJson = CreateWebJsonOptions();

    private static readonly string[] UiCompositionTypePrefixes =
    [
        "ArtworkStack",
        "CardVariant",
        "Cinematic",
        "CollectionPage",
        "Discovery",
        "Hero",
        "LibraryBrowsePreset",
        "MediaHub",
        "MediaSectionNavigation",
        "MediaTile",
        "PosterItem",
    ];

    [Fact]
    public void EngineClientWireCompatibility_MatchesApprovedFixture()
    {
        var actual = BuildClientCompatibilitySnapshot();
        SnapshotFixture.Verify("wire-compatibility.approved.txt", actual, "TUVIMA_UPDATE_WIRE_SNAPSHOT");
    }

    [Fact]
    public void ExportedWireTypeInventory_MatchesApprovedFixture()
    {
        var actual = BuildTypeInventory();
        SnapshotFixture.Verify("wire-type-inventory.approved.txt", actual, "TUVIMA_UPDATE_WIRE_SNAPSHOT");
    }

    [Fact]
    public void DashboardBoundarySelection_ExcludesUiCompositionModels()
    {
        var webTypes = GetWireTypes()
            .Where(candidate => candidate.Scope == "web-client")
            .Select(candidate => candidate.Type.Name)
            .ToArray();

        Assert.DoesNotContain(
            webTypes,
            name => UiCompositionTypePrefixes.Any(
                prefix => name.StartsWith(prefix, StringComparison.Ordinal)));
    }

    [Fact]
    public void StableShapeIds_IgnoreClrLocationAndTypeName()
    {
        var shapes = new WireShapeCatalog();

        Assert.Equal(
            shapes.GetShapeId(typeof(MediaEngine.Api.Models.UniverseCandidateDto), ShapeDirection.Read),
            shapes.GetShapeId(typeof(UniverseCandidateViewModel), ShapeDirection.Read));
        Assert.Equal(
            shapes.GetShapeId(typeof(MediaEngine.Contracts.Library.UnlinkedWorkDto), ShapeDirection.Read),
            shapes.GetShapeId(typeof(UnlinkedWorkViewModel), ShapeDirection.Read));
        Assert.Equal(
            shapes.GetShapeId(typeof(SystemStatusResponse), ShapeDirection.Read),
            shapes.GetShapeId(typeof(SystemStatusViewModel), ShapeDirection.Read));
    }

    private static string BuildClientCompatibilitySnapshot()
    {
        var builder = new StringBuilder();
        var shapes = new WireShapeCatalog();
        builder.AppendLine("# Tuvima Engine client JSON compatibility snapshot");
        builder.AppendLine("# Stable key: IEngineApiClient method + parameter/response role.");
        builder.AppendLine("# CLR namespaces and DTO type names are intentionally absent.");
        builder.AppendLine("# Serializer: System.Text.Json JsonSerializerDefaults.Web; DefaultIgnoreCondition=Never");
        builder.AppendLine("# A pure Api/Web-to-Contracts type move must leave this file byte-identical.");
        builder.AppendLine();

        var methods = typeof(IEngineApiClient)
            .GetMethods()
            .Where(method => !method.IsSpecialName)
            .OrderBy(method => method.Name, StringComparer.Ordinal)
            .ThenBy(method => method.GetParameters().Length)
            .ToArray();

        foreach (var method in methods)
        {
            builder.AppendLine($"[method] {method.Name}");
            foreach (var parameter in method.GetParameters())
            {
                if (parameter.ParameterType == typeof(CancellationToken))
                {
                    continue;
                }

                builder.Append("  input ")
                    .Append(parameter.Name)
                    .Append(" | default=")
                    .Append(GetParameterDefault(parameter))
                    .Append(" | shape=")
                    .Append(shapes.GetShapeId(parameter.ParameterType, ShapeDirection.Write))
                    .AppendLine();
            }

            builder.Append("  response | shape=")
                .Append(shapes.GetShapeId(
                    UnwrapAsyncReturnType(method.ReturnType),
                    ShapeDirection.Read))
                .AppendLine();
            builder.AppendLine();
        }

        builder.AppendLine("# Structurally deduplicated shapes. IDs are SHA-256 of wire semantics,");
        builder.AppendLine("# so equivalent DTOs share an ID regardless of CLR namespace or type name.");
        builder.AppendLine();
        foreach (var shape in shapes.Definitions.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            builder.AppendLine($"[shape] {shape.Key}");
            builder.Append(shape.Value);
            builder.AppendLine();
        }

        return builder.ToString().ReplaceLineEndings("\n");
    }

    private static string BuildTypeInventory()
    {
        var candidates = GetWireTypes();
        var builder = new StringBuilder();
        builder.AppendLine("# Tuvima exported JSON wire-type inventory");
        builder.AppendLine("# Diagnostic inventory: CLR locations may change during contract consolidation.");
        builder.AppendLine("# Compatibility is enforced separately by wire-compatibility.approved.txt.");
        builder.AppendLine("# Serializer: System.Text.Json JsonSerializerDefaults.Web; DefaultIgnoreCondition=Never");
        builder.AppendLine("# Regenerate intentionally with TUVIMA_UPDATE_WIRE_SNAPSHOT=1.");
        builder.AppendLine(
            $"# scopes: contracts={candidates.Count(candidate => candidate.Scope == "contracts")}; " +
            $"api={candidates.Count(candidate => candidate.Scope == "api")}; " +
            $"web-client={candidates.Count(candidate => candidate.Scope == "web-client")}");
        builder.AppendLine();

        foreach (var candidate in candidates)
        {
            AppendType(builder, candidate);
        }

        return builder.ToString().ReplaceLineEndings("\n");
    }

    private static Type UnwrapAsyncReturnType(Type type)
    {
        if (type == typeof(Task) || type == typeof(ValueTask))
        {
            return typeof(void);
        }

        if (type.IsGenericType
            && (type.GetGenericTypeDefinition() == typeof(Task<>)
                || type.GetGenericTypeDefinition() == typeof(ValueTask<>)))
        {
            return type.GetGenericArguments()[0];
        }

        return type;
    }

    private static string GetParameterDefault(ParameterInfo parameter)
    {
        if (!parameter.HasDefaultValue)
        {
            return "required";
        }

        if (parameter.DefaultValue is null)
        {
            return "null";
        }

        if (parameter.DefaultValue == DBNull.Value || parameter.DefaultValue == Missing.Value)
        {
            return "default";
        }

        return parameter.DefaultValue is string text
            ? JsonSerializer.Serialize(text, WebJson)
            : Convert.ToString(parameter.DefaultValue, CultureInfo.InvariantCulture) ?? "null";
    }

    private static bool TryGetDictionaryTypes(
        Type type,
        [NotNullWhen(true)] out Type? keyType,
        [NotNullWhen(true)] out Type? valueType)
    {
        var dictionaryType = GetSelfAndInterfaces(type)
            .FirstOrDefault(candidate =>
                candidate.IsGenericType
                && (candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>)
                    || candidate.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));

        if (dictionaryType is null)
        {
            keyType = null;
            valueType = null;
            return false;
        }

        var arguments = dictionaryType.GetGenericArguments();
        keyType = arguments[0];
        valueType = arguments[1];
        return true;
    }

    private static bool TryGetCollectionElementType(
        Type type,
        [NotNullWhen(true)] out Type? elementType)
    {
        if (type.IsArray)
        {
            elementType = type.GetElementType()!;
            return true;
        }

        if (type == typeof(string))
        {
            elementType = null;
            return false;
        }

        var enumerableType = GetSelfAndInterfaces(type)
            .FirstOrDefault(candidate =>
                candidate.IsGenericType
                && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        if (enumerableType is null)
        {
            elementType = null;
            return false;
        }

        elementType = enumerableType.GetGenericArguments()[0];
        return true;
    }

    private static IEnumerable<Type> GetSelfAndInterfaces(Type type)
    {
        yield return type;
        foreach (var implementedInterface in type.GetInterfaces())
        {
            yield return implementedInterface;
        }
    }

    private static string? GetScalarWireKind(Type type)
    {
        if (type == typeof(void))
        {
            return "none";
        }

        if (type == typeof(object))
        {
            return "any-json";
        }

        if (type == typeof(string) || type == typeof(char) || type == typeof(Uri))
        {
            return "string";
        }

        if (type == typeof(bool))
        {
            return "boolean";
        }

        if (type == typeof(byte) || type == typeof(sbyte)
            || type == typeof(short) || type == typeof(ushort)
            || type == typeof(int) || type == typeof(uint)
            || type == typeof(long) || type == typeof(ulong)
            || type == typeof(float) || type == typeof(double)
            || type == typeof(decimal))
        {
            return "number";
        }

        if (type == typeof(Guid))
        {
            return "guid-string";
        }

        if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
        {
            return "date-time-string";
        }

        if (type == typeof(DateOnly))
        {
            return "date-string";
        }

        if (type == typeof(TimeOnly) || type == typeof(TimeSpan))
        {
            return "time-string";
        }

        if (type == typeof(JsonElement) || type == typeof(JsonDocument))
        {
            return "json-value";
        }

        return null;
    }

    private static string GetPolymorphism(Type type)
    {
        var polymorphic = type.GetCustomAttribute<JsonPolymorphicAttribute>();
        var derived = type.GetCustomAttributes<JsonDerivedTypeAttribute>()
            .Select(attribute => attribute.TypeDiscriminator switch
            {
                null => "none",
                string text => JsonSerializer.Serialize(text, WebJson),
                _ => Convert.ToString(attribute.TypeDiscriminator, CultureInfo.InvariantCulture) ?? "none",
            })
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        if (polymorphic is null && derived.Length == 0)
        {
            return "none";
        }

        var discriminator = polymorphic?.TypeDiscriminatorPropertyName ?? "$type";
        return $"property={discriminator},derived=[{string.Join(",", derived)}]";
    }

    private static IReadOnlyList<WireTypeCandidate> GetWireTypes()
    {
        var contractsAssembly = typeof(DetailPageViewModel).Assembly;
        var apiAssembly = typeof(SystemStatusResponse).Assembly;
        var webAssembly = typeof(IEngineApiClient).Assembly;

        var candidates = new List<WireTypeCandidate>();
        candidates.AddRange(
            contractsAssembly
                .GetExportedTypes()
                .Where(type => type.Namespace?.StartsWith("MediaEngine.Contracts.", StringComparison.Ordinal) == true)
                .Where(IsSerializableCandidate)
                .Select(type => new WireTypeCandidate("contracts", type)));

        candidates.AddRange(
            apiAssembly
                .GetExportedTypes()
                .Where(IsApiBoundaryType)
                .Where(IsSerializableCandidate)
                .Select(type => new WireTypeCandidate("api", type)));

        candidates.AddRange(
            DiscoverDashboardBoundaryTypes(webAssembly)
                .Where(IsSerializableCandidate)
                .Select(type => new WireTypeCandidate("web-client", type)));

        return candidates
            .DistinctBy(candidate => (candidate.Scope, candidate.Type))
            .OrderBy(candidate => candidate.Scope, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Type.FullName, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsApiBoundaryType(Type type)
    {
        if (type.Namespace?.StartsWith("MediaEngine.Api.Models", StringComparison.Ordinal) == true
            || type.Namespace?.StartsWith("MediaEngine.Api.Contracts", StringComparison.Ordinal) == true)
        {
            return true;
        }

        if (type.Namespace?.StartsWith("MediaEngine.Api.Endpoints", StringComparison.Ordinal) != true)
        {
            return false;
        }

        return !type.Name.EndsWith("Endpoints", StringComparison.Ordinal)
            && !type.Name.EndsWith("Endpoint", StringComparison.Ordinal)
            && !type.Name.EndsWith("EndpointExtensions", StringComparison.Ordinal)
            && !type.Name.EndsWith("EndpointLog", StringComparison.Ordinal);
    }

    private static IReadOnlyCollection<Type> DiscoverDashboardBoundaryTypes(Assembly webAssembly)
    {
        var roots = new HashSet<Type>();

        AddMethodBoundaryTypes(typeof(IEngineApiClient).GetMethods(), roots);

        var focusedClients = webAssembly
            .GetExportedTypes()
            .Where(type => type.Namespace == "MediaEngine.Web.Services.Integration.Clients")
            .Where(type => type.Name.EndsWith("Client", StringComparison.Ordinal));
        foreach (var client in focusedClients)
        {
            AddMethodBoundaryTypes(
                client.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly),
                roots);
        }

        var discovered = new HashSet<Type>();
        foreach (var root in roots)
        {
            AddDashboardDtoGraph(root, webAssembly, discovered);
        }

        return discovered;
    }

    private static void AddMethodBoundaryTypes(IEnumerable<MethodInfo> methods, ISet<Type> roots)
    {
        foreach (var method in methods)
        {
            AddContainedTypes(method.ReturnType, roots);
            foreach (var parameter in method.GetParameters())
            {
                AddContainedTypes(parameter.ParameterType, roots);
            }
        }
    }

    private static void AddContainedTypes(Type type, ISet<Type> types)
    {
        if (type.IsByRef || type.IsPointer || type.IsArray)
        {
            AddContainedTypes(type.GetElementType()!, types);
            return;
        }

        var nullableInner = Nullable.GetUnderlyingType(type);
        if (nullableInner is not null)
        {
            AddContainedTypes(nullableInner, types);
            return;
        }

        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
            {
                AddContainedTypes(argument, types);
            }

            return;
        }

        types.Add(type);
    }

    private static void AddDashboardDtoGraph(Type type, Assembly webAssembly, ISet<Type> discovered)
    {
        if (type.Assembly != webAssembly
            || type.Namespace != "MediaEngine.Web.Models.ViewDTOs"
            || IsUiCompositionType(type)
            || !discovered.Add(type))
        {
            return;
        }

        foreach (var property in GetSerializableProperties(type))
        {
            var contained = new HashSet<Type>();
            AddContainedTypes(property.PropertyType, contained);
            foreach (var child in contained)
            {
                AddDashboardDtoGraph(child, webAssembly, discovered);
            }
        }
    }

    private static bool IsUiCompositionType(Type type) =>
        UiCompositionTypePrefixes.Any(
            prefix => type.Name.StartsWith(prefix, StringComparison.Ordinal));

    private static bool IsSerializableCandidate(Type type) =>
        !type.IsGenericTypeDefinition
        && !type.IsPointer
        && !typeof(Delegate).IsAssignableFrom(type)
        && (type.IsEnum
            || type.IsValueType
            || (type.IsClass && !(type.IsAbstract && type.IsSealed)));

    private static void AppendType(StringBuilder builder, WireTypeCandidate candidate)
    {
        var type = candidate.Type;
        builder.AppendLine($"[{candidate.Scope}] {GetFriendlyTypeName(type)}");
        builder.Append("  kind=").Append(GetTypeKind(type));
        builder.Append(" | converter=").Append(GetConverterName(type.GetCustomAttribute<JsonConverterAttribute>()));
        builder.Append(" | number-handling=").Append(GetNumberHandling(type.GetCustomAttribute<JsonNumberHandlingAttribute>()));
        builder.Append(" | unmapped=").Append(GetUnmappedMemberHandling(type));
        builder.AppendLine();

        if (type.IsEnum)
        {
            foreach (var value in Enum.GetValues(type).Cast<object>())
            {
                var memberName = Enum.GetName(type, value)!;
                var numericValue = GetEnumNumericValue(value, type);
                var wireValue = JsonSerializer.Serialize(value, type, WebJson);
                builder.AppendLine($"  enum {memberName}={numericValue} | json={wireValue}");
            }
        }
        else
        {
            var nullability = new NullabilityInfoContext();
            foreach (var property in GetSerializableProperties(type))
            {
                AppendProperty(builder, nullability, property);
            }

            var sample = TryCreateRepresentativeJson(type);
            if (sample is not null)
            {
                builder.AppendLine($"  sample={sample}");
            }
        }

        builder.AppendLine();
    }

    private static void AppendProperty(
        StringBuilder builder,
        NullabilityInfoContext nullability,
        PropertyInfo property)
    {
        var ignore = property.GetCustomAttribute<JsonIgnoreAttribute>();
        var propertyOrder = property.GetCustomAttribute<JsonPropertyOrderAttribute>()?.Order ?? 0;
        var jsonName = GetEffectiveJsonName(property);

        builder.Append("  ")
            .Append(property.Name)
            .Append(": ")
            .Append(GetFriendlyTypeName(property.PropertyType))
            .Append(" | json=")
            .Append(jsonName)
            .Append(" | nullability=")
            .Append(GetNullabilityState(nullability, property))
            .Append(" | accessors=")
            .Append(GetAccessorShape(property))
            .Append(" | required=")
            .Append(IsRequired(property) ? "yes" : "no")
            .Append(" | order=")
            .Append(propertyOrder)
            .Append(" | ignore=")
            .Append(GetIgnoreCondition(ignore))
            .Append(" | converter=")
            .Append(GetConverterName(property.GetCustomAttribute<JsonConverterAttribute>()))
            .Append(" | number-handling=")
            .Append(GetNumberHandling(property.GetCustomAttribute<JsonNumberHandlingAttribute>()))
            .Append(" | extension-data=")
            .Append(property.IsDefined(typeof(JsonExtensionDataAttribute)) ? "yes" : "no")
            .AppendLine();
    }

    private static IReadOnlyList<PropertyInfo> GetSerializableProperties(Type type) =>
        type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.GetIndexParameters().Length == 0)
            .Where(property =>
                property.GetMethod?.IsPublic == true
                || property.SetMethod?.IsPublic == true
                || property.IsDefined(typeof(JsonIncludeAttribute)))
            .OrderBy(property => property.GetCustomAttribute<JsonPropertyOrderAttribute>()?.Order ?? 0)
            .ThenBy(property => property.MetadataToken)
            .ToArray();

    private static IReadOnlyList<PropertyInfo> GetDirectionalProperties(
        Type type,
        ShapeDirection direction) =>
        GetSerializableProperties(type)
            .Where(property =>
                property.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition
                != JsonIgnoreCondition.Always)
            .Where(property => direction switch
            {
                ShapeDirection.Write =>
                    property.GetMethod?.IsPublic == true
                    || property.IsDefined(typeof(JsonIncludeAttribute)),
                ShapeDirection.Read =>
                    property.SetMethod?.IsPublic == true
                    || property.IsDefined(typeof(JsonIncludeAttribute))
                    || IsConstructorBound(type, property),
                _ => false,
            })
            .ToArray();

    private static bool IsConstructorBound(Type type, PropertyInfo property) =>
        type.GetConstructors(BindingFlags.Instance | BindingFlags.Public)
            .SelectMany(constructor => constructor.GetParameters())
            .Any(parameter =>
                string.Equals(
                    parameter.Name,
                    property.Name,
                    StringComparison.OrdinalIgnoreCase));

    private static string? TryCreateRepresentativeJson(Type type)
    {
        if (!CanSafelyInstantiate(type))
        {
            return null;
        }

        object instance;
        try
        {
            instance = Activator.CreateInstance(type)!;
        }
        catch
        {
            return null;
        }

        foreach (var property in GetSerializableProperties(type))
        {
            if (property.SetMethod?.IsPublic != true
                || property.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition == JsonIgnoreCondition.Always)
            {
                continue;
            }

            if (TryCreateSentinelValue(property.PropertyType, out var value))
            {
                property.SetValue(instance, value);
            }
            else
            {
                property.SetValue(
                    instance,
                    property.PropertyType.IsValueType
                        ? Activator.CreateInstance(property.PropertyType)
                        : null);
            }
        }

        return JsonSerializer.Serialize(instance, type, WebJson);
    }

    private static bool CanSafelyInstantiate(Type type)
    {
        if (!type.IsValueType
            && (type.IsAbstract || type.GetConstructor(Type.EmptyTypes) is null))
        {
            return false;
        }

        // Auto-properties keep the representative sample free of behaviorful
        // getters (for example, presentation helpers on Dashboard view models).
        return GetSerializableProperties(type)
            .Where(property => property.GetMethod?.IsPublic == true)
            .All(property =>
                property.SetMethod?.IsPublic == true
                && HasCompilerGeneratedBackingField(type, property));
    }

    private static bool HasCompilerGeneratedBackingField(Type type, PropertyInfo property)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var field = current.GetField(
                $"<{property.Name}>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (field?.IsDefined(typeof(CompilerGeneratedAttribute)) == true)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryCreateSentinelValue(Type type, [NotNullWhen(true)] out object? value)
    {
        var nullableInner = Nullable.GetUnderlyingType(type);
        if (nullableInner is not null)
        {
            return TryCreateSentinelValue(nullableInner, out value);
        }

        if (type == typeof(string))
        {
            value = "sample";
            return true;
        }

        if (type == typeof(bool))
        {
            value = true;
            return true;
        }

        if (type == typeof(Guid))
        {
            value = Guid.Parse("11111111-2222-3333-4444-555555555555");
            return true;
        }

        if (type == typeof(DateTime))
        {
            value = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            return true;
        }

        if (type == typeof(DateTimeOffset))
        {
            value = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero);
            return true;
        }

        if (type == typeof(TimeSpan))
        {
            value = TimeSpan.FromSeconds(7);
            return true;
        }

        if (type == typeof(Uri))
        {
            value = new Uri("https://example.invalid/sample", UriKind.Absolute);
            return true;
        }

        if (type == typeof(JsonElement))
        {
            using var document = JsonDocument.Parse("""{"sample":true}""");
            value = document.RootElement.Clone();
            return true;
        }

        if (type.IsEnum)
        {
            var values = Enum.GetValues(type);
            value = (values.Length == 0 ? Activator.CreateInstance(type) : values.GetValue(0))!;
            return true;
        }

        if (type.IsPrimitive || type == typeof(decimal))
        {
            value = Convert.ChangeType(7, type);
            return true;
        }

        if (type.IsArray)
        {
            value = Array.CreateInstance(type.GetElementType()!, 0);
            return true;
        }

        if (TryCreateEmptyCollection(type, out value))
        {
            return true;
        }

        value = null;
        return false;
    }

    private static bool TryCreateEmptyCollection(Type type, [NotNullWhen(true)] out object? value)
    {
        if (!type.IsGenericType)
        {
            value = null;
            return false;
        }

        var definition = type.GetGenericTypeDefinition();
        var arguments = type.GetGenericArguments();
        Type? concreteType = null;

        if (definition == typeof(IEnumerable<>)
            || definition == typeof(IReadOnlyCollection<>)
            || definition == typeof(IReadOnlyList<>)
            || definition == typeof(ICollection<>)
            || definition == typeof(IList<>))
        {
            concreteType = typeof(List<>).MakeGenericType(arguments);
        }
        else if (definition == typeof(ISet<>)
                 || definition == typeof(IReadOnlySet<>))
        {
            concreteType = typeof(HashSet<>).MakeGenericType(arguments);
        }
        else if (definition == typeof(IDictionary<,>)
                 || definition == typeof(IReadOnlyDictionary<,>))
        {
            concreteType = typeof(Dictionary<,>).MakeGenericType(arguments);
        }
        else if (!type.IsAbstract && type.GetConstructor(Type.EmptyTypes) is not null
                 && typeof(IEnumerable).IsAssignableFrom(type))
        {
            concreteType = type;
        }

        value = concreteType is null ? null : Activator.CreateInstance(concreteType);
        return value is not null;
    }

    private static JsonSerializerOptions CreateWebJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            WriteIndented = false,
        };
        options.MakeReadOnly();
        return options;
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

    private static bool IsInitOnly(MethodInfo setMethod) =>
        setMethod.ReturnParameter
            .GetRequiredCustomModifiers()
            .Contains(typeof(IsExternalInit));

    private static bool IsRequired(PropertyInfo property) =>
        property.IsDefined(typeof(RequiredMemberAttribute))
        || property.IsDefined(typeof(JsonRequiredAttribute));

    private static string GetIgnoreCondition(JsonIgnoreAttribute? attribute) =>
        attribute is null ? "never(global)" : attribute.Condition.ToString();

    private static string GetEffectiveJsonName(PropertyInfo property) =>
        property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
        ?? WebJson.PropertyNamingPolicy!.ConvertName(property.Name);

    private static string GetConverterName(JsonConverterAttribute? attribute)
    {
        var converterType = attribute?.ConverterType;
        if (converterType is null)
        {
            return "default";
        }

        var name = converterType.IsGenericType
            ? converterType.GetGenericTypeDefinition().Name
            : converterType.Name;
        var tickIndex = name.IndexOf('`', StringComparison.Ordinal);
        return tickIndex < 0 ? name : name[..tickIndex];
    }

    private static string GetEnumNumericValue(object value, Type enumType)
    {
        var underlyingValue = Convert.ChangeType(
            value,
            Enum.GetUnderlyingType(enumType),
            CultureInfo.InvariantCulture);
        return Convert.ToString(underlyingValue, CultureInfo.InvariantCulture)!;
    }

    private static string GetNumberHandling(JsonNumberHandlingAttribute? attribute) =>
        attribute?.Handling.ToString() ?? "default";

    private static string GetUnmappedMemberHandling(Type type) =>
        type.GetCustomAttribute<JsonUnmappedMemberHandlingAttribute>()?.UnmappedMemberHandling.ToString()
        ?? "default";

    private static string GetTypeKind(Type type)
    {
        if (type.IsEnum)
        {
            return "enum";
        }

        if (type.IsValueType)
        {
            return "struct";
        }

        return type.IsAbstract ? "abstract-class" : "class";
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
            return type.FullName?.Replace('+', '.') ?? type.Name;
        }

        var typeDefinitionName = type.GetGenericTypeDefinition().FullName ?? type.Name;
        var tickIndex = typeDefinitionName.IndexOf('`', StringComparison.Ordinal);
        if (tickIndex >= 0)
        {
            typeDefinitionName = typeDefinitionName[..tickIndex];
        }

        var arguments = string.Join(", ", type.GetGenericArguments().Select(GetFriendlyTypeName));
        return $"{typeDefinitionName.Replace('+', '.')}<{arguments}>";
    }

    private sealed class WireShapeCatalog
    {
        private readonly Dictionary<(Type Type, ShapeDirection Direction), string> _shapeIdsByType = [];
        private readonly Dictionary<string, string> _definitions = new(StringComparer.Ordinal);
        private readonly HashSet<(Type Type, ShapeDirection Direction)> _building = [];

        public IReadOnlyDictionary<string, string> Definitions => _definitions;

        public string GetShapeId(Type type, ShapeDirection direction)
        {
            var key = (type, direction);
            if (_shapeIdsByType.TryGetValue(key, out var existing))
            {
                return existing;
            }

            if (!_building.Add(key))
            {
                return Register($"direction={GetDirectionName(direction)} | kind=recursive-object\n");
            }

            var definition = BuildDefinition(type, direction);
            _building.Remove(key);

            var shapeId = Register(definition);
            _shapeIdsByType[key] = shapeId;
            return shapeId;
        }

        private string BuildDefinition(Type type, ShapeDirection direction)
        {
            var builder = new StringBuilder();
            builder.Append("direction=")
                .Append(GetDirectionName(direction))
                .Append(" | ");

            var nullableInner = Nullable.GetUnderlyingType(type);
            if (nullableInner is not null)
            {
                builder.Append("kind=nullable | inner=")
                    .Append(GetShapeId(nullableInner, direction))
                    .AppendLine();
                return builder.ToString();
            }

            if (TryGetDictionaryTypes(type, out var keyType, out var valueType))
            {
                builder.Append("kind=map | key=")
                    .Append(GetShapeId(keyType, direction))
                    .Append(" | value=")
                    .Append(GetShapeId(valueType, direction))
                    .AppendLine();
                return builder.ToString();
            }

            if (TryGetCollectionElementType(type, out var elementType))
            {
                builder.Append("kind=array | item=")
                    .Append(GetShapeId(elementType, direction))
                    .AppendLine();
                return builder.ToString();
            }

            var scalarKind = GetScalarWireKind(type);
            if (scalarKind is not null)
            {
                builder.Append("kind=scalar | wire=")
                    .Append(scalarKind)
                    .AppendLine();
                return builder.ToString();
            }

            if (type.IsEnum)
            {
                builder.Append("kind=enum")
                    .Append(" | converter=")
                    .Append(GetConverterName(type.GetCustomAttribute<JsonConverterAttribute>()))
                    .Append(" | number-handling=")
                    .Append(GetNumberHandling(type.GetCustomAttribute<JsonNumberHandlingAttribute>()))
                    .AppendLine();

                foreach (var value in Enum.GetValues(type).Cast<object>())
                {
                    builder.Append("value=")
                        .Append(GetEnumNumericValue(value, type))
                        .Append(" | json=")
                        .Append(JsonSerializer.Serialize(value, type, WebJson))
                        .AppendLine();
                }

                return builder.ToString();
            }

            if (!IsSerializableCandidate(type))
            {
                builder.AppendLine("kind=opaque");
                return builder.ToString();
            }

            builder.Append("kind=object")
                .Append(" | converter=")
                .Append(GetConverterName(type.GetCustomAttribute<JsonConverterAttribute>()))
                .Append(" | number-handling=")
                .Append(GetNumberHandling(type.GetCustomAttribute<JsonNumberHandlingAttribute>()))
                .Append(" | unmapped=")
                .Append(GetUnmappedMemberHandling(type))
                .Append(" | polymorphism=")
                .Append(GetPolymorphism(type))
                .AppendLine();

            var nullability = new NullabilityInfoContext();
            foreach (var property in GetDirectionalProperties(type, direction))
            {
                var ignore = property.GetCustomAttribute<JsonIgnoreAttribute>();
                builder.Append("property=")
                    .Append(GetEffectiveJsonName(property))
                    .Append(" | nullability=")
                    .Append(GetNullabilityState(nullability, property))
                    .Append(" | accessors=")
                    .Append(GetAccessorShape(property))
                    .Append(" | required=")
                    .Append(IsRequired(property) ? "yes" : "no")
                    .Append(" | order=")
                    .Append(property.GetCustomAttribute<JsonPropertyOrderAttribute>()?.Order ?? 0)
                    .Append(" | ignore=")
                    .Append(GetIgnoreCondition(ignore))
                    .Append(" | converter=")
                    .Append(GetConverterName(property.GetCustomAttribute<JsonConverterAttribute>()))
                    .Append(" | number-handling=")
                    .Append(GetNumberHandling(property.GetCustomAttribute<JsonNumberHandlingAttribute>()))
                    .Append(" | extension-data=")
                    .Append(property.IsDefined(typeof(JsonExtensionDataAttribute)) ? "yes" : "no");

                if (ignore?.Condition != JsonIgnoreCondition.Always)
                {
                    builder.Append(" | shape=")
                        .Append(GetShapeId(property.PropertyType, direction));
                }

                builder.AppendLine();
            }

            if (direction == ShapeDirection.Write)
            {
                var sample = TryCreateRepresentativeJson(type);
                if (sample is not null)
                {
                    builder.Append("sample=")
                        .Append(sample)
                        .AppendLine();
                }
            }

            return builder.ToString();
        }

        private string Register(string definition)
        {
            var id = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(definition)))
                .ToLowerInvariant();
            if (_definitions.TryGetValue(id, out var existing))
            {
                Assert.Equal(existing, definition);
            }
            else
            {
                _definitions.Add(id, definition);
            }

            return id;
        }

        private static string GetDirectionName(ShapeDirection direction) =>
            direction == ShapeDirection.Read ? "read" : "write";
    }

    private enum ShapeDirection
    {
        Read,
        Write,
    }

    private sealed record WireTypeCandidate(string Scope, Type Type);
}
