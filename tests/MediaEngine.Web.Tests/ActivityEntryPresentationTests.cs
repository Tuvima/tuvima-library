using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Tests;

public sealed class ActivityEntryPresentationTests
{
    [Fact]
    public void SharedActivityEntry_UsesDashboardPresentationHelpers()
    {
        var entry = new ActivityEntryResponse
        {
            ActionType = "MediaAdded",
            OccurredAt = DateTimeOffset.UtcNow.ToString("O"),
            ChangesJson = """{"title":"Example","entity_id":"11111111-2222-3333-4444-555555555555"}""",
        };

        var richData = entry.GetRichData();

        Assert.NotNull(richData);
        Assert.Equal("Example", richData.Title);
        Assert.Equal("just now", entry.GetRelativeTime());
    }
}
