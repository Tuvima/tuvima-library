using System.Net;
using System.Net.Http.Json;
using System.Text;
using MediaEngine.Contracts.Approvals;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Tests;

public sealed class ApprovalResponsesTests
{
    [Fact]
    public async Task ApprovalRequired409_IsReadWithItsPreviews()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = JsonContent.Create(new ApprovalRequiredResponse
            {
                Approvals = [new ApprovalPreviewResponse { Kind = "plugin", ItemId = "p", Title = "P", StatementHash = "h" }],
            }),
        };

        var approval = await ApprovalResponses.TryReadAsync(response, null, default);

        var preview = Assert.Single(approval!.Approvals);
        Assert.Equal("h", preview.StatementHash);
    }

    [Fact]
    public async Task AnOrdinaryConflict_IsNotAnApprovalRequest_AndItsBodyCanStillBeRead()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent("""{"title":"Conflict.","detail":"Needs Library 2.0.0 or later."}""", Encoding.UTF8, "application/problem+json"),
        };

        var approval = await ApprovalResponses.TryReadAsync(response, null, default);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Null(approval);
        Assert.Contains("Needs Library 2.0.0 or later.", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task OtherStatuses_AreNeverAnApprovalRequest(HttpStatusCode status)
    {
        using var response = new HttpResponseMessage(status) { Content = JsonContent.Create(new ApprovalRequiredResponse()) };

        Assert.Null(await ApprovalResponses.TryReadAsync(response, null, default));
    }

    [Fact]
    public async Task AnApprovalBodyWithNoPreviews_IsNotAnApprovalRequest()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Conflict) { Content = JsonContent.Create(new ApprovalRequiredResponse()) };

        Assert.Null(await ApprovalResponses.TryReadAsync(response, null, default));
    }
}
