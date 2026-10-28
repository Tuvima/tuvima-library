using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Contracts.Paging;
using MediaEngine.Contracts.Persons;
using MediaEngine.Contracts.Playback;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Models;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Branding;
using MediaEngine.Web.Services.Integration.Clients;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    // -- GET /persons (libraryItem list) ------------------------------------

    public async Task<IReadOnlyList<PersonListItemResponse>?> GetPersonsAsync(
        string? role = null, int offset = 0, int limit = 200, CancellationToken ct = default)
    {
        try
        {
            var safeOffset = Math.Max(0, offset);
            var safeLimit = Math.Clamp(limit <= 0 ? 200 : limit, 1, 500);
            var url = $"/persons?offset={safeOffset}&limit={safeLimit}";
            if (!string.IsNullOrWhiteSpace(role))
            {
                url += $"&role={Uri.EscapeDataString(role)}";
            }

            var page = await ReadPersonPageAsync(url, ct);
            return page?.Items;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /persons failed");
            return null;
        }
    }

    /// <summary>
    /// Returns a canonical, owned-library people catalog page for presentation
    /// surfaces such as Collections / People.
    /// </summary>
    public async Task<PagedResponse<PersonListItemResponse>?> GetPersonsPageAsync(
        string? search = null,
        string? role = null,
        int offset = 0,
        int limit = 100,
        string? lane = null,
        string? sort = null,
        CancellationToken ct = default)
    {
        try
        {
            var safeOffset = Math.Max(0, offset);
            var safeLimit = Math.Clamp(limit <= 0 ? 100 : limit, 1, 500);
            var url = $"/persons?catalog=true&offset={safeOffset}&limit={safeLimit}";
            if (!string.IsNullOrWhiteSpace(search))
            {
                url += $"&q={Uri.EscapeDataString(search)}";
            }

            if (!string.IsNullOrEmpty(role))
            {
                url += $"&role={Uri.EscapeDataString(role)}";
            }

            if (!string.IsNullOrWhiteSpace(lane))
            {
                url += $"&lane={Uri.EscapeDataString(lane)}";
            }

            if (string.Equals(sort, "count", StringComparison.OrdinalIgnoreCase))
            {
                url += "&sort=count";
            }

            return await ReadPersonPageAsync(url, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /persons failed");
            return null;
        }
    }

    // -- GET /persons/by-collection/{collectionId} -------------------------------------

    public async Task<List<PersonViewModel>> GetPersonsByRoleAsync(
        string role, int limit = 50, CancellationToken ct = default)
    {
        try
        {
            var safeLimit = Math.Clamp(limit <= 0 ? 50 : limit, 1, 500);
            var payload = await _http.GetFromJsonAsync<JsonElement>(
                $"/persons?role={Uri.EscapeDataString(role)}&limit={safeLimit}", ct);
            List<PersonListItemResponse>? raw;
            if (payload.ValueKind == JsonValueKind.Array)
            {
                raw = payload.Deserialize<List<PersonListItemResponse>>();
            }
            else if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("items", out var items))
            {
                raw = items.Deserialize<List<PersonListItemResponse>>();
            }
            else
            {
                raw = [];
            }
            return raw?.Select(p =>
            {
                var headshotUrl = ResolvePersonHeadshotUrl(
                    p.id,
                    p.has_local_headshot,
                    p.headshot_url);
                return new PersonViewModel
                {
                    Id = p.id,
                    Name = p.name,
                    Roles = p.roles,
                    WikidataQid = p.wikidata_qid,
                    HeadshotUrl = headshotUrl,
                    HasLocalHeadshot = p.has_local_headshot,
                    LocalHeadshotUrl = headshotUrl,
                    Biography = p.biography,
                    Occupation = p.occupation,
                };
            }).ToList() ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /persons?role={Role} failed", role);
            LastError = ex.Message;
            return [];
        }
    }

    public async Task<List<PersonViewModel>> GetPersonsByCollectionAsync(
        Guid collectionId, CancellationToken ct = default)
    {
        try
        {
            var raw = await _http.GetFromJsonAsync<List<PersonSummaryResponse>>(
                $"/persons/by-collection/{collectionId}", ct);
            return raw?.Select(MapPersonSummary).ToList() ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /persons/by-collection/{CollectionId} failed", collectionId);
            LastError = ex.Message;
            return [];
        }
    }

    public async Task<List<PersonViewModel>> GetPersonsByWorkAsync(
        Guid workId, CancellationToken ct = default)
    {
        try
        {
            var raw = await _http.GetFromJsonAsync<List<PersonSummaryResponse>>(
                $"/persons/by-work/{workId}", ct);
            return raw?.Select(MapPersonSummary).ToList() ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /persons/by-work/{WorkId} failed", workId);
            LastError = ex.Message;
            return [];
        }
    }

    // -- GET /persons/role-counts ------------------------------------------

    public async Task<Dictionary<string, int>> GetPersonRoleCountsAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _http.GetFromJsonAsync<Dictionary<string, int>>(
                "/persons/role-counts?catalog=true",
                ct);
            return result ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /persons/role-counts failed");
            return new();
        }
    }

    // -- GET /persons/presence?ids=... -------------------------------------

    public async Task<Dictionary<string, Dictionary<string, int>>> GetPersonPresenceAsync(
        IEnumerable<Guid> personIds, CancellationToken ct = default)
    {
        try
        {
            var ids = string.Join(",", personIds);
            var result = await _http.GetFromJsonAsync<Dictionary<string, Dictionary<string, int>>>(
                $"/persons/presence?ids={ids}", ct);
            return result ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /persons/presence failed");
            return new();
        }
    }

    private async Task<PagedResponse<PersonListItemResponse>?> ReadPersonPageAsync(
        string url,
        CancellationToken ct)
    {
        var page = await _http.GetFromJsonAsync<PagedResponse<PersonListItemResponse>>(url, ct);
        if (page is null)
        {
            return null;
        }

        var normalized = page.Items
            .Select(person => person with
            {
                headshot_url = ResolvePersonHeadshotUrl(person.id, person.has_local_headshot, person.headshot_url),
            })
            .ToList();
        return page with { Items = normalized };
    }

    // -- GET /collections/{id}/related -------------------------------------------------

    public async Task<RelatedCollectionsViewModel?> GetRelatedCollectionsAsync(
        Guid collectionId, int limit = 20, CancellationToken ct = default)
    {
        try
        {
            var raw = await _http.GetFromJsonAsync<MediaEngine.Contracts.Collections.RelatedCollectionsResponse>(
                $"/collections/{collectionId}/related?limit={limit}", ct);
            if (raw is null)
            {
                return null;
            }

            return new RelatedCollectionsViewModel
            {
                SectionTitle = raw.SectionTitle,
                Reason = raw.Reason,
                Collections = raw.Collections.Select(MapCollection).ToList(),
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /collections/{CollectionId}/related failed", collectionId);
            LastError = ex.Message;
            return null;
        }
    }

    // -- GET /persons/{id} (detail) ------------------------------------------

    public async Task<PersonDetailViewModel?> GetPersonDetailAsync(
        Guid personId, CancellationToken ct = default)
    {
        try
        {
            var raw = await _http.GetFromJsonAsync<PersonDetailResponse>(
                $"/persons/{personId}", ct);
            if (raw is null)
            {
                return null;
            }

            return new PersonDetailViewModel
            {
                Id = raw.Id,
                Name = raw.Name ?? string.Empty,
                Roles = raw.Roles.ToList(),
                HeadshotUrl = raw.HeadshotUrl,
                HasLocalHeadshot = raw.HasLocalHeadshot,
                LocalHeadshotUrl = !string.IsNullOrWhiteSpace(raw.HeadshotUrl)
                    ? AbsoluteUrl(raw.HeadshotUrl)
                    : raw.HasLocalHeadshot ? AbsoluteUrl($"/persons/{raw.Id}/headshot") : null,
                Biography = raw.Biography,
                Occupation = raw.Occupation,
                DateOfBirth = raw.DateOfBirth,
                DateOfDeath = raw.DateOfDeath,
                PlaceOfBirth = raw.PlaceOfBirth,
                PlaceOfDeath = raw.PlaceOfDeath,
                Nationality = raw.Nationality,
                WikidataQid = raw.WikidataQid,
                Instagram = raw.Instagram,
                Twitter = raw.Twitter,
                TikTok = raw.TikTok,
                Mastodon = raw.Mastodon,
                Website = raw.Website,
                IsGroup = raw.IsGroup,
                GroupMembers = raw.GroupMembers.Select(MapGroupMember).ToList(),
                MemberOfGroups = raw.MemberOfGroups.Select(MapGroupMember).ToList(),
                BannerUrl = raw.BannerUrl is not null ? AbsoluteUrl(raw.BannerUrl) : null,
                BackgroundUrl = raw.BackgroundUrl is not null ? AbsoluteUrl(raw.BackgroundUrl) : null,
                LogoUrl = raw.LogoUrl is not null ? AbsoluteUrl(raw.LogoUrl) : null,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /persons/{PersonId} failed", personId);
            LastError = ex.Message;
            return null;
        }
    }

    public async Task<List<PersonLibraryCreditViewModel>> GetPersonLibraryCreditsAsync(
        Guid personId, CancellationToken ct = default)
    {
        try
        {
            var credits = await _http.GetFromJsonAsync<List<PersonLibraryCreditDto>>(
                $"/persons/{personId}/library-credits", ct);
            return credits?.Select(MapPersonLibraryCredit).ToList() ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /persons/{PersonId}/library-credits failed", personId);
            LastError = ex.Message;
            return [];
        }
    }

    // -- GET /persons/{id}/works -----------------------------------------------

    public async Task<List<CollectionViewModel>> GetWorksByPersonAsync(
        Guid personId, CancellationToken ct = default)
    {
        try
        {
            var raw = await _http.GetFromJsonAsync<List<MediaEngine.Contracts.Collections.CollectionDto>>(
                $"/persons/{personId}/works", ct);
            return raw?.Select(MapCollection).ToList() ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /persons/{PersonId}/works failed", personId);
            LastError = ex.Message;
            return [];
        }
    }

    // -- GET /persons/{id}/aliases --------------------------------------------

    /// <inheritdoc/>
    public async Task<PersonAliasResponse?> GetPersonAliasesAsync(Guid personId, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync($"persons/{personId}/aliases", ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<PersonAliasResponse>(cancellationToken: ct);
            return result is null
                ? null
                : new PersonAliasResponse
                {
                    PersonId = result.PersonId,
                    PersonName = result.PersonName,
                    IsPseudonym = result.IsPseudonym,
                    Aliases = result.Aliases.Select(alias => new PersonAliasItemResponse
                    {
                        Id = alias.Id,
                        Name = alias.Name,
                        Roles = alias.Roles,
                        HeadshotUrl = string.IsNullOrWhiteSpace(alias.HeadshotUrl)
                            ? null
                            : AbsoluteUrl(alias.HeadshotUrl),
                        IsPseudonym = alias.IsPseudonym,
                        WikidataQid = alias.WikidataQid,
                        Relationship = alias.Relationship,
                    }).ToList(),
                };
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /persons/{PersonId}/aliases failed", personId);
            return null;
        }
    }

    private PersonViewModel MapPersonSummary(PersonSummaryResponse person)
    {
        var headshotUrl = ResolvePersonHeadshotUrl(
            person.Id,
            person.HasLocalHeadshot,
            person.HeadshotUrl);
        return new PersonViewModel
        {
            Id = person.Id,
            Name = person.Name,
            Roles = person.Roles.ToList(),
            WikidataQid = person.WikidataQid,
            HeadshotUrl = headshotUrl,
            HasLocalHeadshot = person.HasLocalHeadshot,
            LocalHeadshotUrl = headshotUrl,
            Biography = person.Biography,
            Occupation = person.Occupation,
        };
    }

    private PersonLibraryCreditViewModel MapPersonLibraryCredit(PersonLibraryCreditDto credit) => new()
    {
        WorkId = credit.WorkId,
        CollectionId = credit.CollectionId,
        MediaType = credit.MediaType,
        Title = credit.Title,
        CoverUrl = string.IsNullOrWhiteSpace(credit.CoverUrl) ? credit.CoverUrl : AbsoluteUrl(credit.CoverUrl),
        Year = credit.Year,
        Role = credit.Role,
        Characters = credit.Characters.Select(character => new CharacterPortrayalDto
        {
            FictionalEntityId = character.FictionalEntityId,
            CharacterName = character.CharacterName,
            CharacterQid = character.CharacterQid,
            PortraitUrl = string.IsNullOrWhiteSpace(character.PortraitUrl)
                ? character.PortraitUrl
                : AbsoluteUrl(character.PortraitUrl),
        }).ToList(),
    };

    private static GroupMemberView MapGroupMember(PersonGroupMemberDto groupMember) =>
        new(groupMember.Id, groupMember.Name, groupMember.DateRange);

}
