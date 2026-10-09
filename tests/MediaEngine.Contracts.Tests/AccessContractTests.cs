using System.Reflection;
using System.Text.Json;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Authorization;

namespace MediaEngine.Contracts.Tests;

public sealed class AccessContractTests
{
    [Fact]
    public void NativeClientScopesAddExplicitEventConsentWithoutChangingDefaultPlaybackConsent()
    {
        var expected = ApplicationPermissionIds.NativeClient.Select(id => id.Value).ToArray();

        Assert.Equal(expected, ClientApiScopes.Consumer);
        Assert.Equal(new[]
        {
            "library.read", "artwork.read", "library.changes.read", "progress.read", "progress.write", "queue.read",
            "queue.write", "playback.read", "playback.write", "downloads.read", "downloads.write", "events.subscribe",
        }, ClientApiScopes.Consumer);
        Assert.Equal(new[] { "library.read", "artwork.read", "progress.read", "progress.write",
            "queue.read", "queue.write", "playback.read", "playback.write" }, ClientApiScopes.Default);

        var request = new PairingDecisionRequest
        {
            UserCode = "ABCD-EFGH",
            Approved = true,
            Scopes = [ClientApiScopes.LibraryRead],
        };
        Assert.Equal(
            "{\"user_code\":\"ABCD-EFGH\",\"approved\":true,\"scopes\":[\"library.read\"]}",
            JsonSerializer.Serialize(request));
    }

    [Fact]
    public void AccessResponsesContainNoSecretOrHashProperties()
    {
        var responseTypes = new[]
        {
            typeof(AccountAccessResponse), typeof(AccountProfileGrantDto), typeof(GrantAdminProtectionDto),
            typeof(AccountSelfServiceResponse), typeof(ApplicationResponse),
            typeof(ApplicationCredentialResponse), typeof(ApplicationPermissionDefinitionDto),
            typeof(AuthorizationAuditEntryResponse),
        };

        foreach (var type in responseTypes)
        {
            Assert.DoesNotContain(type.GetProperties(BindingFlags.Instance | BindingFlags.Public), property =>
                property.Name.Contains("Hash", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("Plaintext", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void PlaintextCredentialExistsOnlyOnTheDedicatedIssueResponse()
    {
        var accessTypes = typeof(ApplicationResponse).Assembly.GetExportedTypes()
            .Where(type => type.Namespace == typeof(ApplicationResponse).Namespace)
            .ToArray();
        var plaintextProperties = accessTypes
            .SelectMany(type => type.GetProperties().Select(property => (Type: type, Property: property)))
            .Where(entry => entry.Property.Name.Contains("PlaintextCredential", StringComparison.Ordinal))
            .ToArray();

        var entry = Assert.Single(plaintextProperties);
        Assert.Equal(typeof(ApplicationCredentialIssuedResponse), entry.Type);
        Assert.Equal(typeof(string), entry.Property.PropertyType);
    }

    [Fact]
    public void ApplicationTypeAndEventEnvelopeUseStableJsonNames()
    {
        Assert.Equal("\"ServerIntegration\"", JsonSerializer.Serialize(ApplicationTypeDto.ServerIntegration));

        using var payload = JsonDocument.Parse("{\"state\":\"healthy\"}");
        var envelope = new ApplicationEventEnvelope(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            "system.health_changed",
            1,
            DateTimeOffset.Parse("2026-09-08T12:00:00Z"),
            "server-1",
            new ApplicationEventSubjectDto("server", "server-1", null, null),
            payload.RootElement.Clone());

        var json = JsonSerializer.Serialize(envelope);
        Assert.Contains("\"event_id\":", json, StringComparison.Ordinal);
        Assert.Contains("\"event_type\":\"system.health_changed\"", json, StringComparison.Ordinal);
        Assert.Contains("\"occurred_at\":", json, StringComparison.Ordinal);
        Assert.Contains("\"server_id\":\"server-1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"payload\":{\"state\":\"healthy\"}", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AccountIdentityCarriesItsEmailAndNoLocalOnlyFlag()
    {
        var response = new AccountAccessResponse(
            Guid.NewGuid(), "owner@example.com", true, false, 1, [], [], [],
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);
        var json = JsonSerializer.Serialize(response);

        Assert.Contains("\"email\":\"owner@example.com\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("local_only", json, StringComparison.Ordinal);
    }
}
