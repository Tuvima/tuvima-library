using MediaEngine.Domain.Models;
using MediaEngine.Providers.Services;
using MediaEngine.Providers.Workers;

namespace MediaEngine.Providers.Tests;

public sealed class ExternalPersonIdChoiceTests
{
    private const string BeatlesId = "b10bbbfc-cf9e-42e0-be17-e2c3e1d2600d";

    private static Dictionary<string, PersonEnrichmentWorker.TmdbPersonImageHint> TmdbHints(string name, int id) => new()
    {
        [$"Actor::{RetailHints.NormalizePersonNameKey(name)}"] = new PersonEnrichmentWorker.TmdbPersonImageHint(id, null),
    };

    [Fact]
    public void ExternalIdFor_PrefersTheMusicBrainzArtistId()
    {
        var person = new PersonReference("Performer", "The Beatles", null, false);
        var artists = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["The Beatles"] = BeatlesId };

        var id = PersonEnrichmentWorker.ExternalIdFor(person, artists, new Dictionary<string, PersonEnrichmentWorker.TmdbPersonImageHint>());

        Assert.Equal(new ExternalPersonId("P434", BeatlesId), id);
    }

    [Fact]
    public void ExternalIdFor_UsesTheTmdbPersonIdForAFilmActor()
    {
        var person = new PersonReference("Actor", "Anne Hathaway", null, false);

        var id = PersonEnrichmentWorker.ExternalIdFor(person, new Dictionary<string, string>(), TmdbHints("Anne Hathaway", 1813));

        Assert.Equal(new ExternalPersonId("P4985", "1813"), id);
    }

    [Fact]
    public void ExternalIdFor_ReturnsNothingWhenNoProviderGaveAnId()
    {
        var person = new PersonReference("Actor", "Nobody Atall", null, false);

        Assert.Null(PersonEnrichmentWorker.ExternalIdFor(person, new Dictionary<string, string>(), TmdbHints("Someone Else", 5)));
    }
}
