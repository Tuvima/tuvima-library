using System.Text.Json;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.Notifications;

/// <summary>One calm update waiting for the daily summary.</summary>
internal sealed record PendingSummaryItem(string ChannelId, string Title, string Detail, DateTimeOffset At);

/// <summary>
/// Holds the calm updates that wait for the daily summary, and remembers which day it last went out so it goes once a day.
/// Kept in the data store so a restart does not lose what is waiting.
/// </summary>
internal sealed class NotificationSummaryService(INotificationRepository repository, TimeProvider clock)
{
    private const string Key = "summary_pending";
    public const int MaxWaiting = 100;

    private readonly SemaphoreSlim _gate = new(1, 1);

    private sealed class Document
    {
        public string LastSentOn { get; set; } = string.Empty;

        public List<PendingSummaryItem> Items { get; set; } = [];
    }

    public async Task AddAsync(string channelId, string title, string detail, DateTimeOffset at, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var document = await LoadAsync(ct).ConfigureAwait(false);
            document.Items.Add(new PendingSummaryItem(channelId, title, detail, at));
            if (document.Items.Count > MaxWaiting)
            {
                document.Items.RemoveRange(0, document.Items.Count - MaxWaiting);
            }

            await SaveAsync(document, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> IsDueAsync(string day, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return !string.Equals((await LoadAsync(ct).ConfigureAwait(false)).LastSentOn, day, StringComparison.Ordinal);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Empties the waiting list and marks the day as done, so the summary cannot go out twice.</summary>
    public async Task<IReadOnlyList<PendingSummaryItem>> TakeAsync(string day, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var document = await LoadAsync(ct).ConfigureAwait(false);
            var items = document.Items;
            document.Items = [];
            document.LastSentOn = day;
            await SaveAsync(document, ct).ConfigureAwait(false);
            return items;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Document> LoadAsync(CancellationToken ct)
    {
        var json = await repository.GetSettingAsync(Key, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Document();
        }

        try
        {
            return JsonSerializer.Deserialize<Document>(json, MediaEngineJson.Web) ?? new Document();
        }
        catch (JsonException)
        {
            // A damaged row only loses what was waiting for the summary.
            return new Document();
        }
    }

    private Task SaveAsync(Document document, CancellationToken ct) =>
        repository.SetSettingAsync(Key, JsonSerializer.Serialize(document, MediaEngineJson.Web), clock.GetUtcNow(), ct);
}

/// <summary>Checks once a minute whether the daily summary is due.</summary>
internal sealed class NotificationSummaryWorker(NotificationService service, ILogger<NotificationSummaryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await service.SendDueSummaryAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "The daily summary could not be sent.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The Engine is stopping.
        }
    }
}
