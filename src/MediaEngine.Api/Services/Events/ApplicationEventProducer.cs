using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Events;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.Events;

public sealed class ApplicationEventProducer(
    IDatabaseConnection database,
    IApplicationEventOutboxWriter outbox,
    ApplicationEventRegistry registry,
    IVaultLibraryRepository vaultLibraries) : IApplicationEventProducer
{
    public async Task PublishAsync(ApplicationEventDraft value, CancellationToken ct = default)
    {
        if (!registry.TryGet(value.EventType, out _))
        {
            throw new ArgumentException($"Unknown application event type '{value.EventType}'.", nameof(value));
        }

        // The Vault never reaches an app or a webhook (Videos spec D17): an event about an item in a Vault library is dropped here,
        // for every event type and every sender, before it is stored.
        if (value.Subject.LibraryId is { } libraryId
            && (await vaultLibraries.GetIdsAsync(ct).ConfigureAwait(false)).Contains(libraryId.ToString("D")))
        {
            return;
        }

        await database.ExecuteWriteAsync(
            (connection, transaction, token) => outbox.Append(connection, transaction, value, token), ct).ConfigureAwait(false);
    }
}
