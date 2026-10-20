using System.Text.Json;
using MediaEngine.Contracts.Approvals;
using MediaEngine.Contracts.Authentication;

namespace MediaEngine.Contracts.Tests;

public sealed class ApprovalContractTests
{
    [Fact]
    public void ApprovalRequiredResponse_UsesTheDocumentedWireNames()
    {
        var response = new ApprovalRequiredResponse
        {
            Approvals =
            [
                new ApprovalPreviewResponse
                {
                    Kind = ApprovalKinds.Plugin,
                    ItemId = "tuvima.test",
                    Title = "Test",
                    KindLabel = "Plugin",
                    CanDo = ["Reads your media files"],
                    SendsDataOut = true,
                    SendsDataTo = "the internet",
                    Notes = ["A note"],
                    StatementHash = "abc",
                },
            ],
        };

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(response));

        Assert.Equal("approval_required", json.RootElement.GetProperty("error").GetString());
        var preview = json.RootElement.GetProperty("approvals")[0];
        Assert.Equal(
            ["kind", "item_id", "title", "kind_label", "can_do", "sends_data_out", "sends_data_to", "notes", "statement_hash"],
            preview.EnumerateObject().Select(property => property.Name).ToArray());
    }

    [Fact]
    public void ApprovalRecordResponse_CarriesWhoWhenAndTheStatementShown()
    {
        var record = new ApprovalRecordResponse
        {
            ApprovedByName = "Avery",
            ApprovedAt = new DateTimeOffset(2026, 10, 19, 9, 30, 0, TimeSpan.Zero),
            StatementHash = "abc",
        };

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(record));

        Assert.Equal(
            ["approved_by_name", "approved_at", "statement", "statement_hash"],
            json.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
    }

    [Fact]
    public void ActionsThatNeedAnApproval_AcceptTheHashUnderApprovalHash()
    {
        var plugin = JsonSerializer.Deserialize<PluginEnableRequest>("""{"approval_hash":"h1"}""")!;
        var credential = JsonSerializer.Deserialize<CreateApplicationCredentialRequest>("""{"name":"Key","expires_at":null,"approval_hash":"h2"}""")!;
        var webhook = JsonSerializer.Deserialize<SaveApplicationWebhookRequest>(
            """{"url":"https://a.example/x","event_types":["a"],"allow_local_network":false,"is_enabled":true,"expected_version":null,"approval_hash":"h3"}""")!;
        var withoutHash = JsonSerializer.Deserialize<SaveApplicationWebhookRequest>(
            """{"url":"https://a.example/x","event_types":["a"],"allow_local_network":false,"is_enabled":true,"expected_version":null}""")!;

        Assert.Equal("h1", plugin.ApprovalHash);
        Assert.Equal("h2", credential.ApprovalHash);
        Assert.Equal("h3", webhook.ApprovalHash);
        Assert.Null(withoutHash.ApprovalHash);
    }

    [Fact]
    public void WideningAnIntegrationsKeys_AcceptsTheHashUnderApprovalHash_AndAPluginReportsNeedsApproval()
    {
        var permissions = JsonSerializer.Deserialize<SetApplicationPermissionsRequest>("""{"permission_ids":["library.read"],"approval_hash":"h4"}""")!;
        var update = JsonSerializer.Deserialize<UpdateApplicationRequest>(
            """{"name":"N","description":null,"application_type":"ServerIntegration","is_enabled":true,"is_administrator":false,"approval_hash":"h5"}""")!;
        var withoutHash = JsonSerializer.Deserialize<SetApplicationPermissionsRequest>("""{"permission_ids":[]}""")!;
        var plugin = JsonSerializer.Serialize(new MediaEngine.Contracts.Plugins.PluginSummaryResponse { NeedsApproval = true });

        Assert.Equal("h4", permissions.ApprovalHash);
        Assert.Equal("h5", update.ApprovalHash);
        Assert.Null(withoutHash.ApprovalHash);
        Assert.Contains("\"needs_approval\":true", plugin, StringComparison.Ordinal);
        Assert.Equal("application", ApprovalKinds.Application);
    }

    [Fact]
    public void PreviewRequest_RoundTripsAWebhookAndACredential()
    {
        var request = new ApprovalPreviewRequest
        {
            Kind = ApprovalKinds.Webhook,
            Webhook = new ApprovalWebhookInput { Url = "https://a.example/x", EventTypes = ["a"], AllowLocalNetwork = true },
            Credential = new ApprovalCredentialInput { ApplicationId = Guid.Parse("11111111-2222-3333-4444-555555555555") },
        };

        var round = JsonSerializer.Deserialize<ApprovalPreviewRequest>(JsonSerializer.Serialize(request))!;

        Assert.Equal("webhook", round.Kind);
        Assert.True(round.Webhook!.AllowLocalNetwork);
        Assert.Equal(request.Credential.ApplicationId, round.Credential!.ApplicationId);
    }
}
