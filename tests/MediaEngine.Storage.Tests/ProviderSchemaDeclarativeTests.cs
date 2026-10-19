using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaEngine.Domain.Configuration;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// The provider schema documents the declarative provider format. It must name exactly the
/// properties the loader understands, so a new property cannot be added to one and forgotten in
/// the other.
/// </summary>
public sealed class ProviderSchemaDeclarativeTests
{
    private static readonly (string Pointer, Type Type)[] Objects =
    [
        ("$defs/lookupFollowUp", typeof(LookupFollowUpConfiguration)),
        ("$defs/lookupFollowUp/properties/cache", typeof(FollowUpCacheConfiguration)),
        ("$defs/claimSort", typeof(ClaimSortConfiguration)),
        ("$defs/claimRule", typeof(ClaimRuleConfiguration)),
        ("$defs/claimRule/properties/join", typeof(ClaimJoinConfiguration)),
        ("$defs/claimRule/properties/role", typeof(ClaimRoleConfiguration)),
        ("$defs/claimRule/properties/role/properties/map/items", typeof(ClaimRoleMapEntry)),
        ("$defs/claimRule/properties/joined/items", typeof(ClaimJoinedConfiguration)),
        ("$defs/claimRule/properties/item", typeof(SequenceItemConfiguration)),
        ("$defs/claimRule/properties/emit/items", typeof(SequenceEmitConfiguration)),
        ("$defs/claimRule/properties/manifest", typeof(SequenceManifestRuleConfiguration)),
    ];

    [Fact]
    public void ProviderSchema_DeclaresExactlyThePropertiesTheLoaderReads()
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(FindRepoRoot(), "config", "schemas", "providers", "provider.schema.json")));

        foreach (var (pointer, type) in Objects)
        {
            var node = pointer.Split('/').Aggregate(schema.RootElement, (current, part) => current.GetProperty(part));
            var declared = node.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order().ToArray();
            var read = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name)
                .Where(name => name is not null)
                .Select(name => name!)
                .Order()
                .ToArray();

            Assert.True(declared.SequenceEqual(read),
                $"{pointer}: schema declares [{string.Join(", ", declared)}] but {type.Name} reads [{string.Join(", ", read)}]");
        }
    }

    [Fact]
    public void ProviderSchema_ListsFollowUpsAtTheTopLevel()
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(FindRepoRoot(), "config", "schemas", "providers", "provider.schema.json")));

        Assert.True(schema.RootElement.GetProperty("properties").TryGetProperty("follow_ups", out _));
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "MediaEngine.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("Could not find repository root.");
    }
}
