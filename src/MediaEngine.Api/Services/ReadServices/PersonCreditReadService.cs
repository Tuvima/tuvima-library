using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Application.Services;
using MediaEngine.Contracts.Persons;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.ReadServices;

public sealed class PersonCreditReadService : IPersonCreditReadService
{
    // The detail projection must retain every canonical cast credit. Its
    // presentation layer bounds the visible preview separately.
    private const int MaxCastCredits = int.MaxValue;

    private readonly ICanonicalValueArrayRepository _canonicalArrayRepo;
    private readonly IPersonRepository _personRepo;
    private readonly IDatabaseConnection _db;

    public PersonCreditReadService(
        ICanonicalValueArrayRepository canonicalArrayRepo,
        IPersonRepository personRepo,
        IDatabaseConnection db)
    {
        _canonicalArrayRepo = canonicalArrayRepo;
        _personRepo = personRepo;
        _db = db;
    }

    public async Task<List<CastCreditDto>> BuildForWorkAsync(
        Guid workId,
        CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        var work = await conn.QueryFirstOrDefaultAsync<WorkIdentityRow>(
            """
            SELECT w.id AS WorkId,
                   COALESCE(
                       NULLIF(TRIM(w.wikidata_qid), ''),
                       (SELECT cv.value
                        FROM editions e
                        INNER JOIN media_assets ma ON ma.edition_id = e.id
                        INNER JOIN canonical_values cv ON cv.entity_id = ma.id
                        WHERE e.work_id = w.id AND cv.key = 'wikidata_qid'
                        LIMIT 1),
                       (SELECT cv.value
                        FROM canonical_values cv
                        WHERE cv.entity_id = w.id AND cv.key = 'wikidata_qid'
                        LIMIT 1),
                       (SELECT ij.resolved_qid
                        FROM editions e
                        INNER JOIN media_assets ma ON ma.edition_id = e.id
                        INNER JOIN identity_jobs ij ON ij.entity_id = ma.id
                        WHERE e.work_id = w.id
                          AND ij.resolved_qid IS NOT NULL
                          AND TRIM(ij.resolved_qid) <> ''
                        ORDER BY ij.updated_at DESC, ij.created_at DESC
                        LIMIT 1)
                   ) AS WorkQid,
                   COALESCE(gp.id, p.id, w.id) AS RootWorkId,
                   COALESCE(
                       NULLIF(TRIM(root.wikidata_qid), ''),
                       (SELECT cv.value
                        FROM canonical_values cv
                        WHERE cv.entity_id = root.id AND cv.key = 'wikidata_qid'
                        LIMIT 1),
                       (SELECT cv.value
                        FROM editions e
                        INNER JOIN media_assets ma ON ma.edition_id = e.id
                        INNER JOIN canonical_values cv ON cv.entity_id = ma.id
                        WHERE e.work_id = root.id AND cv.key = 'wikidata_qid'
                        LIMIT 1)
                   ) AS RootWorkQid
            FROM works w
            LEFT JOIN works p
                ON p.id = w.parent_work_id
            LEFT JOIN works gp
                ON gp.id = p.parent_work_id
            LEFT JOIN works root
                ON root.id = COALESCE(gp.id, p.id, w.id)
            WHERE w.id = @workId
            LIMIT 1;
            """,
            new { workId });

        if (work is null)
        {
            return [];
        }

        var workRankMap = await CastRankMap.BuildAsync(work.WorkId, _canonicalArrayRepo, _db, ct);
        var credits = new List<CastCreditDto>();
        var workClaimCredits = await BuildFallbackCreditsFromMetadataClaimsAsync(
            workId,
            _personRepo,
            _db,
            ct);
        var rootRankMap = work.RootWorkId.HasValue
            ? await CastRankMap.BuildAsync(work.RootWorkId.Value, _canonicalArrayRepo, _db, ct)
            : CastRankMap.Empty;
        var rootClaimCredits = work.RootWorkId.HasValue && work.RootWorkId.Value != workId
            ? await BuildFallbackCreditsFromMetadataClaimsAsync(
                work.RootWorkId.Value,
                _personRepo,
                _db,
                ct)
            : [];
        var workUsesRootIdentity = work.RootWorkId.HasValue
            && work.RootWorkId.Value != workId
            && workClaimCredits.Count == 0
            && rootClaimCredits.Count > 0
            && (string.IsNullOrWhiteSpace(work.RootWorkQid)
                || string.Equals(work.WorkQid, work.RootWorkQid, StringComparison.OrdinalIgnoreCase));
        var explicitCredits = string.IsNullOrWhiteSpace(work.WorkQid) || workUsesRootIdentity
            ? []
            : await BuildExplicitCastAsync(work.WorkQid, workRankMap, _db, ct);
        await RemoveUncorroboratedOrdinalCharacterLinksAsync(
            workId,
            explicitCredits,
            workClaimCredits,
            _canonicalArrayRepo,
            ct);
        AddUniqueCredits(credits, explicitCredits);

        var effectiveRootWorkQid = StringHelpers.FirstNonBlank(
            work.RootWorkQid,
            workUsesRootIdentity ? work.WorkQid : null);
        var rootExplicitCredits = string.IsNullOrWhiteSpace(effectiveRootWorkQid)
            || !work.RootWorkId.HasValue
            || work.RootWorkId.Value == workId
            ? []
            : await BuildExplicitCastAsync(effectiveRootWorkQid, rootRankMap, _db, ct);
        if (work.RootWorkId.HasValue)
        {
            await RemoveUncorroboratedOrdinalCharacterLinksAsync(
                work.RootWorkId.Value,
                rootExplicitCredits,
                rootClaimCredits,
                _canonicalArrayRepo,
                ct);
        }
        AddUniqueCredits(credits, rootExplicitCredits);

        var linkedActors = await BuildActorOnlyCreditsFromMediaLinksAsync(workId, workRankMap, _db, ct);
        AddUniqueCredits(credits, linkedActors);

        var fallbackCredits = await BuildFallbackCreditsFromCanonicalArrayAsync(workId, _canonicalArrayRepo, _personRepo, ct);
        AddUniqueCredits(credits, fallbackCredits);

        AddUniqueCredits(credits, workClaimCredits);

        if (work.RootWorkId.HasValue && work.RootWorkId.Value != workId)
        {
            fallbackCredits = await BuildFallbackCreditsFromCanonicalArrayAsync(work.RootWorkId.Value, _canonicalArrayRepo, _personRepo, ct);
            AddUniqueCredits(credits, fallbackCredits);

            AddUniqueCredits(credits, rootClaimCredits);
        }

        return credits.Take(MaxCastCredits).ToList();
    }

    public async Task<List<CastCreditDto>> BuildForCollectionRootAsync(
        Guid rootWorkId,
        string? rootWorkQid,
        CancellationToken ct)
    {
        var rankMap = await CastRankMap.BuildAsync(rootWorkId, _canonicalArrayRepo, _db, ct);
        var claimCredits = await BuildFallbackCreditsFromMetadataClaimsAsync(
            rootWorkId,
            _personRepo,
            _db,
            ct);
        var explicitCredits = string.IsNullOrWhiteSpace(rootWorkQid)
            ? []
            : await BuildExplicitCastAsync(rootWorkQid, rankMap, _db, ct);
        await RemoveUncorroboratedOrdinalCharacterLinksAsync(
            rootWorkId,
            explicitCredits,
            claimCredits,
            _canonicalArrayRepo,
            ct);
        if (explicitCredits.Count > 0)
        {
            AddUniqueCredits(explicitCredits, claimCredits);
            return explicitCredits;
        }

        var fallbackCredits = await BuildFallbackCreditsFromCanonicalArrayAsync(rootWorkId, _canonicalArrayRepo, _personRepo, ct);
        AddUniqueCredits(explicitCredits, fallbackCredits);
        AddUniqueCredits(explicitCredits, claimCredits);
        return explicitCredits.Take(MaxCastCredits).ToList();
    }

    private static void AddUniqueCredits(List<CastCreditDto> destination, IEnumerable<CastCreditDto> source)
    {
        foreach (var credit in source)
        {
            var duplicate = destination.FirstOrDefault(existing => IsSameCredit(existing, credit));
            if (duplicate is not null)
            {
                MergeCharacterPortrayals(duplicate, credit);
                continue;
            }

            destination.Add(credit);

            if (destination.Count >= MaxCastCredits)
            {
                return;
            }
        }
    }

    private static bool IsSameCredit(CastCreditDto existing, CastCreditDto incoming)
        => (incoming.PersonId.HasValue && existing.PersonId == incoming.PersonId)
            || (!string.IsNullOrWhiteSpace(incoming.WikidataQid)
                && string.Equals(existing.WikidataQid, incoming.WikidataQid, StringComparison.OrdinalIgnoreCase))
            || string.Equals(existing.Name, incoming.Name, StringComparison.OrdinalIgnoreCase);

    private static void MergeCharacterPortrayals(CastCreditDto target, CastCreditDto source)
    {
        foreach (var character in source.Characters)
        {
            var duplicate = target.Characters.Any(existing =>
                (character.FictionalEntityId != Guid.Empty && existing.FictionalEntityId == character.FictionalEntityId)
                || (!string.IsNullOrWhiteSpace(character.CharacterQid)
                    && string.Equals(existing.CharacterQid, character.CharacterQid, StringComparison.OrdinalIgnoreCase))
                || string.Equals(existing.CharacterName, character.CharacterName, StringComparison.OrdinalIgnoreCase));
            if (!duplicate)
            {
                target.Characters.Add(character);
            }
        }
    }

    private static async Task<List<CastCreditDto>> BuildExplicitCastAsync(
        string workQid,
        CastRankMap rankMap,
        IDatabaseConnection db,
        CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        var rows = (await conn.QueryAsync<ExplicitCastRow>(
            """
            SELECT cpl.rowid             AS LinkOrder,
                   p.id                  AS ActorPersonId,
                   p.name                AS ActorName,
                   p.wikidata_qid        AS ActorQid,
                   p.headshot_url        AS ActorHeadshotUrl,
                   p.local_headshot_path AS ActorLocalHeadshotPath,
                   fe.id                 AS CharacterId,
                   fe.label              AS CharacterName,
                   fe.wikidata_qid       AS CharacterQid,
                   fe.image_url          AS CharacterImageUrl,
                   cp.id                 AS PortraitId,
                   cp.image_url          AS PortraitImageUrl,
                   cp.local_image_path   AS PortraitLocalImagePath,
                   cp.is_default         AS PortraitIsDefault
            FROM character_performer_links cpl
            INNER JOIN persons p
                ON p.id = cpl.person_id
            LEFT JOIN fictional_entities fe
                ON fe.id = cpl.fictional_entity_id
            LEFT JOIN character_portraits cp
                ON cp.fictional_entity_id = fe.id
               AND cp.person_id = p.id
            WHERE cpl.work_qid = @workQid
            ORDER BY cpl.rowid, fe.label, cp.is_default DESC;
            """,
            new { workQid })).ToList();

        return rows
            .Where(row => row.ActorPersonId.HasValue && !string.IsNullOrWhiteSpace(row.ActorName))
            .GroupBy(row => new
            {
                row.ActorPersonId,
                row.ActorName,
                row.ActorQid,
                HeadshotUrl = BuildHeadshotUrl(row.ActorPersonId, row.ActorLocalHeadshotPath, row.ActorHeadshotUrl),
            })
            .Select(group =>
            {
                var sourceOrder = group.Min(row => row.LinkOrder);
                var credit = new CastCreditDto
                {
                    PersonId = group.Key.ActorPersonId,
                    Name = group.Key.ActorName ?? "Unknown",
                    WikidataQid = group.Key.ActorQid,
                    HeadshotUrl = group.Key.HeadshotUrl,
                    Characters = group
                        .Where(row => row.CharacterId != Guid.Empty)
                        .GroupBy(row => new { row.CharacterId, row.CharacterName, row.CharacterQid })
                        .Select(characterGroup =>
                        {
                            var preferred = characterGroup
                                .OrderBy(row => row.LinkOrder)
                                .ThenByDescending(row => row.PortraitIsDefault)
                                .ThenByDescending(row => !string.IsNullOrWhiteSpace(row.PortraitImageUrl))
                                .First();

                            return new
                            {
                                LinkOrder = characterGroup.Min(row => row.LinkOrder),
                                Character = new CharacterPortrayalDto
                                {
                                    FictionalEntityId = characterGroup.Key.CharacterId,
                                    CharacterName = characterGroup.Key.CharacterName,
                                    CharacterQid = characterGroup.Key.CharacterQid,
                                    PortraitUrl = ApiImageUrls.BuildCharacterPortraitUrl(
                                        preferred.PortraitId,
                                        preferred.PortraitLocalImagePath,
                                        preferred.PortraitImageUrl),
                                },
                            };
                        })
                        // Wikidata commonly emits a credited alias before the canonical
                        // character identity (for example a concealed identity). Prefer
                        // the final qualified identity while retaining every portrayal.
                        .OrderByDescending(item => item.LinkOrder)
                        .Select(item => item.Character)
                        .ToList(),
                };

                return new RankedCastCredit(
                    credit,
                    rankMap.RankFor(group.Key.ActorQid, group.Key.ActorName) ?? sourceOrder,
                    sourceOrder);
            })
            .OrderBy(item => item.Rank)
            .ThenBy(item => item.SourceOrder)
            .ThenBy(item => item.Credit.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxCastCredits)
            .Select(item => item.Credit)
            .ToList();
    }

    private static async Task RemoveUncorroboratedOrdinalCharacterLinksAsync(
        Guid workId,
        IReadOnlyList<CastCreditDto> explicitCredits,
        IReadOnlyList<CastCreditDto> claimCredits,
        ICanonicalValueArrayRepository canonicalArrayRepo,
        CancellationToken ct)
    {
        if (explicitCredits.Count == 0 || claimCredits.Count == 0)
        {
            return;
        }

        var castMembers = await canonicalArrayRepo.GetValuesAsync(workId, "cast_member", ct);
        var characters = await canonicalArrayRepo.GetValuesAsync(workId, "characters", ct);
        if (castMembers.Count == 0 || characters.Count == 0)
        {
            return;
        }

        var charactersByOrdinal = characters
            .GroupBy(character => character.Ordinal)
            .ToDictionary(group => group.Key, group => group.First());
        var broadcastCharacter = characters.Count == 1 && castMembers.Count > 1
            ? characters[0]
            : null;
        foreach (var credit in explicitCredits)
        {
            var castMember = castMembers.FirstOrDefault(entry => SamePerson(entry, credit));
            var evidence = claimCredits.FirstOrDefault(candidate => SamePerson(candidate, credit));
            credit.Characters.RemoveAll(character =>
                ((castMember is not null
                        && charactersByOrdinal.TryGetValue(castMember.Ordinal, out var ordinalCharacter)
                        && SameCharacter(ordinalCharacter, character))
                    || (broadcastCharacter is not null && SameCharacter(broadcastCharacter, character)))
                && (evidence is null
                    || !evidence.Characters.Any(candidate => SameCharacter(candidate, character))));
        }
    }

    private static bool SamePerson(CanonicalArrayEntry entry, CastCreditDto credit)
    {
        var entryQid = ExtractQid(entry.ValueQid);
        var creditQid = ExtractQid(credit.WikidataQid);
        return (!string.IsNullOrWhiteSpace(entryQid)
                && string.Equals(entryQid, creditQid, StringComparison.OrdinalIgnoreCase))
            || string.Equals(entry.Value.Trim(), credit.Name.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool SamePerson(CastCreditDto left, CastCreditDto right)
    {
        var leftQid = ExtractQid(left.WikidataQid);
        var rightQid = ExtractQid(right.WikidataQid);
        return (!string.IsNullOrWhiteSpace(leftQid)
                && string.Equals(leftQid, rightQid, StringComparison.OrdinalIgnoreCase))
            || string.Equals(left.Name.Trim(), right.Name.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameCharacter(CanonicalArrayEntry entry, CharacterPortrayalDto character)
    {
        var entryQid = ExtractQid(entry.ValueQid);
        var characterQid = ExtractQid(character.CharacterQid);
        return (!string.IsNullOrWhiteSpace(entryQid)
                && string.Equals(entryQid, characterQid, StringComparison.OrdinalIgnoreCase))
            || string.Equals(entry.Value.Trim(), character.CharacterName?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameCharacter(CharacterPortrayalDto left, CharacterPortrayalDto right)
    {
        var leftQid = ExtractQid(left.CharacterQid);
        var rightQid = ExtractQid(right.CharacterQid);
        return (!string.IsNullOrWhiteSpace(leftQid)
                && string.Equals(leftQid, rightQid, StringComparison.OrdinalIgnoreCase))
            || string.Equals(left.CharacterName?.Trim(), right.CharacterName?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<List<CastCreditDto>> BuildActorOnlyCreditsFromMediaLinksAsync(
        Guid workId,
        CastRankMap rankMap,
        IDatabaseConnection db,
        CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        var rows = (await conn.QueryAsync<ActorOnlyRow>(
            """
            SELECT MIN(primary_credit.billing_order) AS LinkOrder,
                   p.id                  AS ActorPersonId,
                   p.name                AS ActorName,
                   p.wikidata_qid        AS ActorQid,
                   p.headshot_url        AS ActorHeadshotUrl,
                   p.local_headshot_path AS ActorLocalHeadshotPath
            FROM primary_person_media_credits primary_credit
            INNER JOIN persons p
                ON p.id = primary_credit.person_id
            INNER JOIN media_assets ma
                ON ma.id = primary_credit.media_asset_id
            INNER JOIN editions e
                ON e.id = ma.edition_id
            WHERE e.work_id = @workId
              AND primary_credit.role = 'Actor'
            GROUP BY p.id, p.name, p.wikidata_qid, p.headshot_url, p.local_headshot_path
            ORDER BY MIN(primary_credit.billing_order);
            """,
            new { workId })).ToList();

        return rows
            .Where(row => row.ActorPersonId.HasValue && !string.IsNullOrWhiteSpace(row.ActorName))
            .Select(row =>
            {
                var credit = new CastCreditDto
                {
                    PersonId = row.ActorPersonId,
                    Name = row.ActorName ?? "Unknown",
                    WikidataQid = row.ActorQid,
                    HeadshotUrl = BuildHeadshotUrl(row.ActorPersonId, row.ActorLocalHeadshotPath, row.ActorHeadshotUrl),
                };

                return new RankedCastCredit(
                    credit,
                    rankMap.RankFor(row.ActorQid, row.ActorName) ?? row.LinkOrder,
                    row.LinkOrder);
            })
            .OrderBy(item => item.Rank)
            .ThenBy(item => item.SourceOrder)
            .ThenBy(item => item.Credit.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxCastCredits)
            .Select(item => item.Credit)
            .ToList();
    }

    private static async Task<List<CastCreditDto>> BuildFallbackCreditsFromCanonicalArrayAsync(
        Guid workId,
        ICanonicalValueArrayRepository canonicalArrayRepo,
        IPersonRepository personRepo,
        CancellationToken ct)
    {
        var credits = new List<CastCreditDto>();
        var castEntries = await canonicalArrayRepo.GetValuesAsync(workId, "cast_member", ct);
        foreach (var entry in castEntries.OrderBy(value => value.Ordinal).Take(MaxCastCredits))
        {
            if (string.IsNullOrWhiteSpace(entry.Value))
            {
                continue;
            }

            Person? person = null;
            var qid = ExtractQid(entry.ValueQid);
            if (!string.IsNullOrWhiteSpace(qid))
            {
                person = await personRepo.FindByQidAsync(qid, ct);
            }

            person ??= await personRepo.FindByNameAsync(entry.Value, ct);

            credits.Add(new CastCreditDto
            {
                PersonId = person?.Id,
                Name = person?.Name ?? entry.Value,
                WikidataQid = qid ?? person?.WikidataQid,
                HeadshotUrl = BuildHeadshotUrl(person?.Id, person?.LocalHeadshotPath, person?.HeadshotUrl),
            });
        }

        return credits;
    }

    private static async Task<List<CastCreditDto>> BuildFallbackCreditsFromMetadataClaimsAsync(
        Guid workId,
        IPersonRepository personRepo,
        IDatabaseConnection db,
        CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        var rows = (await conn.QueryAsync<CastClaimRow>(new CommandDefinition(
            """
            SELECT mc.rowid       AS RowNumber,
                   mc.claim_key   AS ClaimKey,
                   mc.claim_value AS ClaimValue
            FROM metadata_claims mc
            WHERE mc.entity_id = @workId
              AND mc.claim_key IN ('cast_member', 'cast_member_qid', 'cast_member_character')
              AND NULLIF(mc.claim_value, '') IS NOT NULL
            ORDER BY mc.rowid;
            """,
            new { workId },
            cancellationToken: ct))).ToList();

        var entries = BuildCastEntriesFromClaims(rows)
            .Take(MaxCastCredits)
            .ToList();
        var peopleByQid = (await personRepo.FindByQidsAsync(
                entries.Where(entry => !string.IsNullOrWhiteSpace(entry.Qid)).Select(entry => entry.Qid!),
                ct))
            .Where(person => !string.IsNullOrWhiteSpace(person.WikidataQid))
            .GroupBy(person => person.WikidataQid!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var peopleByName = (await personRepo.FindByNamesAsync(
                entries.Select(entry => entry.Name),
                ct))
            .GroupBy(person => person.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);

        var credits = new List<CastCreditDto>(entries.Count);
        foreach (var entry in entries)
        {
            var person = !string.IsNullOrWhiteSpace(entry.Qid)
                ? peopleByQid.GetValueOrDefault(entry.Qid)
                : null;
            person ??= peopleByName.GetValueOrDefault(entry.Name);

            credits.Add(new CastCreditDto
            {
                PersonId = person?.Id,
                Name = person?.Name ?? entry.Name,
                WikidataQid = entry.Qid ?? person?.WikidataQid,
                HeadshotUrl = BuildHeadshotUrl(person?.Id, person?.LocalHeadshotPath, person?.HeadshotUrl),
                Characters = BuildClaimCharacterPortrayals(workId, entry.Name, entry.Characters),
            });
        }

        return credits;
    }

    private static List<CastClaimEntry> BuildCastEntriesFromClaims(IReadOnlyList<CastClaimRow> rows)
    {
        var claimGroups = new List<MutableCastClaimEntry>();
        MutableCastClaimEntry? active = null;
        foreach (var row in rows.OrderBy(row => row.RowNumber))
        {
            if (row.ClaimKey.Equals("cast_member", StringComparison.OrdinalIgnoreCase))
            {
                var name = row.ClaimValue.Trim();
                active = string.IsNullOrWhiteSpace(name)
                    ? null
                    : new MutableCastClaimEntry(name);
                if (active is not null)
                {
                    claimGroups.Add(active);
                }

                continue;
            }

            if (active is null)
            {
                continue;
            }

            if (row.ClaimKey.Equals("cast_member_character", StringComparison.OrdinalIgnoreCase))
            {
                active.Characters.AddRange(SplitCharacterNames(row.ClaimValue));
                continue;
            }

            if (row.ClaimKey.Equals("cast_member_qid", StringComparison.OrdinalIgnoreCase))
            {
                var parsed = ParseQidLabel(row.ClaimValue);
                if (string.IsNullOrWhiteSpace(parsed.Label)
                    || string.Equals(parsed.Label, active.Name, StringComparison.OrdinalIgnoreCase))
                {
                    active.Qid = parsed.Qid;
                }
            }
        }

        var merged = new List<MutableCastClaimEntry>();
        foreach (var group in claimGroups)
        {
            var existing = merged.FirstOrDefault(entry =>
                (!string.IsNullOrWhiteSpace(group.Qid)
                    && string.Equals(entry.Qid, group.Qid, StringComparison.OrdinalIgnoreCase))
                || string.Equals(entry.Name, group.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                merged.Add(group);
                continue;
            }

            existing.Qid ??= group.Qid;
            foreach (var character in group.Characters)
            {
                if (!existing.Characters.Contains(character, StringComparer.OrdinalIgnoreCase))
                {
                    existing.Characters.Add(character);
                }
            }
        }

        return merged
            .Select(entry => new CastClaimEntry(
                entry.Name,
                entry.Qid,
                entry.Characters
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()))
            .ToList();
    }

    private static List<CharacterPortrayalDto> BuildClaimCharacterPortrayals(
        Guid workId,
        string actorName,
        IReadOnlyList<string> characterNames)
        => characterNames
            .Where(characterName => !string.IsNullOrWhiteSpace(characterName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(characterName => new CharacterPortrayalDto
            {
                FictionalEntityId = CreateDeterministicCharacterId(workId, actorName, characterName),
                CharacterName = characterName.Trim(),
            })
            .ToList();

    private static IReadOnlyList<string> SplitCharacterNames(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(" / ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static Guid CreateDeterministicCharacterId(Guid workId, string actorName, string characterName)
        => Hashing.DeterministicGuid($"{workId:D}|{actorName}|{characterName}");

    private static (string? Qid, string? Label) ParseQidLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (null, null);
        }

        var trimmed = value.Trim();
        var delimiter = trimmed.IndexOf("::", StringComparison.Ordinal);
        if (delimiter > 0)
        {
            return (ExtractQid(trimmed[..delimiter]), StringHelpers.FirstNonBlank(trimmed[(delimiter + 2)..], null));
        }

        return (ExtractQid(trimmed), null);
    }

    private static string? ExtractQid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        var delimiter = trimmed.IndexOf("::", StringComparison.Ordinal);
        if (delimiter > 0)
        {
            trimmed = trimmed[..delimiter].Trim();
        }

        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static string? BuildHeadshotUrl(Guid? personId, string? localHeadshotPath, string? remoteHeadshotUrl)
        => personId.HasValue
            ? ApiImageUrls.BuildPersonHeadshotUrl(personId.Value, localHeadshotPath, remoteHeadshotUrl)
            : remoteHeadshotUrl;

    private sealed record RankedCastCredit(CastCreditDto Credit, long Rank, long SourceOrder);

    private sealed class CastRankMap
    {
        public static readonly CastRankMap Empty = new();

        private readonly Dictionary<string, int> _qidRanks = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _nameRanks = new(StringComparer.OrdinalIgnoreCase);

        public static async Task<CastRankMap> BuildAsync(
            Guid workId,
            ICanonicalValueArrayRepository canonicalArrayRepo,
            IDatabaseConnection db,
            CancellationToken ct)
        {
            var map = new CastRankMap();
            var nextRank = 0;

            using var conn = db.CreateConnection();
            var assetCastEntries = (await conn.QueryAsync<CanonicalArrayEntryRow>(new CommandDefinition(
                """
                SELECT cva.ordinal   AS Ordinal,
                       cva.value     AS Value,
                       cva.value_qid AS ValueQid
                FROM editions e
                INNER JOIN media_assets ma
                    ON ma.edition_id = e.id
                INNER JOIN canonical_value_arrays cva
                    ON cva.entity_id = ma.id
                   AND cva.key = 'cast_member'
                WHERE e.work_id = @workId
                ORDER BY cva.ordinal ASC;
                """,
                new { workId },
                cancellationToken: ct))).ToList();

            foreach (var entry in assetCastEntries)
            {
                if (map.Add(entry.ValueQid, entry.Value, nextRank))
                {
                    nextRank++;
                }
            }

            var castEntries = await canonicalArrayRepo.GetValuesAsync(workId, "cast_member", ct);
            foreach (var entry in castEntries.OrderBy(value => value.Ordinal))
            {
                if (map.Add(entry.ValueQid, entry.Value, nextRank))
                {
                    nextRank++;
                }
            }

            var assetRows = (await conn.QueryAsync<CastClaimRow>(new CommandDefinition(
                """
                SELECT mc.rowid       AS RowNumber,
                       mc.claim_key   AS ClaimKey,
                       mc.claim_value AS ClaimValue
                FROM editions e
                INNER JOIN media_assets ma
                    ON ma.edition_id = e.id
                INNER JOIN metadata_claims mc
                    ON mc.entity_id = ma.id
                WHERE e.work_id = @workId
                  AND mc.claim_key IN ('cast_member', 'cast_member_qid')
                  AND NULLIF(mc.claim_value, '') IS NOT NULL
                ORDER BY mc.rowid;
                """,
                new { workId },
                cancellationToken: ct))).ToList();

            foreach (var entry in BuildCastEntriesFromClaims(assetRows))
            {
                if (map.Add(entry.Qid, entry.Name, nextRank))
                {
                    nextRank++;
                }
            }

            var rows = (await conn.QueryAsync<CastClaimRow>(new CommandDefinition(
                """
                SELECT mc.rowid       AS RowNumber,
                       mc.claim_key   AS ClaimKey,
                       mc.claim_value AS ClaimValue
                FROM metadata_claims mc
                WHERE mc.entity_id = @workId
                  AND mc.claim_key IN ('cast_member', 'cast_member_qid')
                  AND NULLIF(mc.claim_value, '') IS NOT NULL
                ORDER BY mc.rowid;
                """,
                new { workId },
                cancellationToken: ct))).ToList();

            foreach (var entry in BuildCastEntriesFromClaims(rows))
            {
                if (map.Add(entry.Qid, entry.Name, nextRank))
                {
                    nextRank++;
                }
            }

            return map;
        }

        public int? RankFor(string? qid, string? name)
        {
            var normalizedQid = NormalizeRankKey(ExtractQid(qid));
            if (!string.IsNullOrWhiteSpace(normalizedQid)
                && _qidRanks.TryGetValue(normalizedQid, out var qidRank))
            {
                return qidRank;
            }

            var normalizedName = NormalizeRankKey(name);
            return !string.IsNullOrWhiteSpace(normalizedName)
                && _nameRanks.TryGetValue(normalizedName, out var nameRank)
                ? nameRank
                : null;
        }

        private bool Add(string? qid, string? name, int rank)
        {
            var added = false;
            var normalizedQid = NormalizeRankKey(ExtractQid(qid));
            if (!string.IsNullOrWhiteSpace(normalizedQid) && !_qidRanks.ContainsKey(normalizedQid))
            {
                _qidRanks[normalizedQid] = rank;
                added = true;
            }

            var normalizedName = NormalizeRankKey(name);
            if (!string.IsNullOrWhiteSpace(normalizedName) && !_nameRanks.ContainsKey(normalizedName))
            {
                _nameRanks[normalizedName] = rank;
                added = true;
            }

            return added;
        }

        private static string? NormalizeRankKey(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return string.Join(' ', value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .ToUpperInvariant();
        }
    }

    private sealed class WorkIdentityRow
    {
        public Guid WorkId { get; init; }
        public string? WorkQid { get; init; }
        public Guid? RootWorkId { get; init; }
        public string? RootWorkQid { get; init; }
    }

    private sealed class ExplicitCastRow
    {
        public long LinkOrder { get; init; }
        public Guid? ActorPersonId { get; init; }
        public string? ActorName { get; init; }
        public string? ActorQid { get; init; }
        public string? ActorHeadshotUrl { get; init; }
        public string? ActorLocalHeadshotPath { get; init; }
        public Guid CharacterId { get; init; }
        public string? CharacterName { get; init; }
        public string? CharacterQid { get; init; }
        public string? CharacterImageUrl { get; init; }
        public Guid? PortraitId { get; init; }
        public string? PortraitImageUrl { get; init; }
        public string? PortraitLocalImagePath { get; init; }
        public bool PortraitIsDefault { get; init; }
    }

    private sealed class ActorOnlyRow
    {
        public long LinkOrder { get; init; }
        public Guid? ActorPersonId { get; init; }
        public string? ActorName { get; init; }
        public string? ActorQid { get; init; }
        public string? ActorHeadshotUrl { get; init; }
        public string? ActorLocalHeadshotPath { get; init; }
    }

    private sealed class CastClaimRow
    {
        public long RowNumber { get; init; }
        public string ClaimKey { get; init; } = string.Empty;
        public string ClaimValue { get; init; } = string.Empty;
    }

    private sealed class CanonicalArrayEntryRow
    {
        public int Ordinal { get; init; }
        public string Value { get; init; } = string.Empty;
        public string? ValueQid { get; init; }
    }

    private sealed record CastClaimEntry(string Name, string? Qid, IReadOnlyList<string> Characters);

    private sealed class MutableCastClaimEntry(string name)
    {
        public string Name { get; } = name;
        public string? Qid { get; set; }
        public List<string> Characters { get; } = [];
    }

    public async Task<List<PersonGroupMemberDto>> GetGroupMembersAsync(
        Guid personId,
        bool isGroup,
        CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        var rows = (await conn.QueryAsync<PersonGroupMemberRow>(
            isGroup
                ? """
                  SELECT p.id AS Id,
                         p.name AS Name,
                         p.headshot_url AS HeadshotUrl,
                         p.local_headshot_path AS LocalHeadshotPath,
                         CASE
                             WHEN pgm.start_date IS NOT NULL AND pgm.end_date IS NOT NULL THEN pgm.start_date || ' - ' || pgm.end_date
                             WHEN pgm.start_date IS NOT NULL THEN pgm.start_date || ' - present'
                             WHEN pgm.end_date IS NOT NULL THEN 'Until ' || pgm.end_date
                             ELSE NULL
                         END AS DateRange
                  FROM person_group_members pgm
                  INNER JOIN persons p ON p.id = pgm.member_id
                  WHERE pgm.group_id = @personId
                  ORDER BY p.name;
                  """
                : """
                  SELECT p.id AS Id,
                         p.name AS Name,
                         p.headshot_url AS HeadshotUrl,
                         p.local_headshot_path AS LocalHeadshotPath,
                         CASE
                             WHEN pgm.start_date IS NOT NULL AND pgm.end_date IS NOT NULL THEN pgm.start_date || ' - ' || pgm.end_date
                             WHEN pgm.start_date IS NOT NULL THEN pgm.start_date || ' - present'
                             WHEN pgm.end_date IS NOT NULL THEN 'Until ' || pgm.end_date
                             ELSE NULL
                         END AS DateRange
                  FROM person_group_members pgm
                  INNER JOIN persons p ON p.id = pgm.group_id
                  WHERE pgm.member_id = @personId
                  ORDER BY p.name;
                  """,
            new { personId })).ToList();

        return rows
            .Select(row => new PersonGroupMemberDto
            {
                Id = row.Id,
                Name = row.Name ?? string.Empty,
                DateRange = row.DateRange,
                HeadshotUrl = ApiImageUrls.BuildPersonHeadshotUrl(
                    row.Id,
                    row.LocalHeadshotPath,
                    row.HeadshotUrl),
            })
            .ToList();
    }

    public async Task<List<PersonLibraryCreditDto>> GetLibraryCreditsAsync(
        Guid personId,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var displayYearSql = MediaDateSql.DisplayOriginalYear(
            "w.id",
            "COALESCE(gp.id, p.id, w.id)",
            "ma.id",
            "w.media_type");
        var baseRows = (await conn.QueryAsync<PersonLibraryCreditRow>(
            $"""
            WITH effective_credit AS (
                SELECT direct_credit.media_asset_id,
                       direct_credit.person_id,
                       direct_credit.role,
                       'Direct' AS association_type,
                       NULL AS via_group_id,
                       NULL AS via_group_name,
                       NULL AS membership_start_date,
                       NULL AS membership_end_date
                FROM primary_person_media_credits direct_credit
                WHERE direct_credit.person_id = @personId

                UNION ALL

                SELECT group_credit.media_asset_id,
                       membership.member_id AS person_id,
                       group_credit.role,
                       'ThroughGroup' AS association_type,
                       membership.group_id AS via_group_id,
                       group_person.name AS via_group_name,
                       membership.start_date AS membership_start_date,
                       membership.end_date AS membership_end_date
                FROM primary_person_media_credits group_credit
                INNER JOIN person_group_members membership
                    ON membership.group_id = group_credit.person_id
                INNER JOIN persons group_person
                    ON group_person.id = membership.group_id
                WHERE membership.member_id = @personId
            )
            SELECT w.id                                   AS WorkId,
                   COALESCE(gp.id, p.id, w.id)             AS RootWorkId,
                   w.collection_id                        AS CollectionId,
                   w.media_type                           AS MediaType,
                   c.display_name                         AS CollectionTitle,
                   c.wikidata_qid                         AS CollectionQid,
                   COALESCE(
                       NULLIF(TRIM(w.wikidata_qid), ''),
                       (SELECT cvq.value
                        FROM canonical_values cvq
                        WHERE cvq.entity_id = ma.id AND cvq.key = 'wikidata_qid'
                        LIMIT 1),
                       (SELECT cvq.value
                        FROM canonical_values cvq
                        WHERE cvq.entity_id = w.id AND cvq.key = 'wikidata_qid'
                        LIMIT 1),
                       (SELECT ij.resolved_qid
                        FROM identity_jobs ij
                        WHERE ij.entity_id = ma.id
                          AND ij.resolved_qid IS NOT NULL
                          AND TRIM(ij.resolved_qid) <> ''
                        ORDER BY ij.updated_at DESC, ij.created_at DESC
                       LIMIT 1)
                   )                                      AS WorkQid,
                   COALESCE(
                       NULLIF(TRIM(COALESCE(gp.wikidata_qid, p.wikidata_qid, w.wikidata_qid)), ''),
                       (SELECT root_qid.value
                        FROM canonical_values root_qid
                        WHERE root_qid.entity_id = COALESCE(gp.id, p.id, w.id)
                          AND root_qid.key = 'wikidata_qid'
                        LIMIT 1)
                   )                                      AS RootWorkQid,
                   COALESCE(
                       MAX(CASE WHEN cv.key = 'title' THEN cv.value END),
                       (SELECT asset_title.value
                        FROM editions asset_edition
                        INNER JOIN media_assets title_asset
                            ON title_asset.edition_id = asset_edition.id
                        INNER JOIN canonical_values asset_title
                            ON asset_title.entity_id = title_asset.id
                           AND asset_title.key = 'title'
                        WHERE asset_edition.work_id = w.id
                        ORDER BY title_asset.id, asset_title.last_scored_at DESC
                        LIMIT 1),
                       c.display_name,
                       'Untitled'
                   )                                      AS Title,
                   (SELECT root_title.value
                    FROM canonical_values root_title
                    WHERE root_title.entity_id = COALESCE(gp.id, p.id, w.id)
                      AND root_title.key = 'title'
                    ORDER BY root_title.last_scored_at DESC
                    LIMIT 1)                              AS RootTitle,
                   {displayYearSql}                       AS Year,
                   primary_credit.role                    AS Role,
                   primary_credit.association_type        AS AssociationType,
                   primary_credit.via_group_id            AS ViaGroupId,
                   primary_credit.via_group_name          AS ViaGroupName,
                   primary_credit.membership_start_date   AS MembershipStartDate,
                   primary_credit.membership_end_date     AS MembershipEndDate,
                   (SELECT root_cover.id
                    FROM entity_assets root_cover
                    WHERE root_cover.entity_id = COALESCE(gp.id, p.id, w.id)
                      AND root_cover.entity_type = 'Work'
                      AND root_cover.asset_type = 'CoverArt'
                    ORDER BY root_cover.is_preferred DESC,
                             root_cover.is_user_override DESC,
                             root_cover.created_at DESC
                    LIMIT 1)                              AS RootCoverAssetId,
                   MIN(ma.id)                             AS FirstAssetId
            FROM effective_credit primary_credit
            INNER JOIN media_assets ma
                ON ma.id = primary_credit.media_asset_id
            INNER JOIN editions e
                ON e.id = ma.edition_id
            INNER JOIN works w
                ON w.id = e.work_id
            LEFT JOIN works p
                ON p.id = w.parent_work_id
            LEFT JOIN works gp
                ON gp.id = p.parent_work_id
            LEFT JOIN collections c
                ON c.id = w.collection_id
            LEFT JOIN canonical_values cv
                ON cv.entity_id = w.id
               AND cv.key = 'title'
            WHERE primary_credit.person_id = @personId
              AND (primary_credit.role != 'Author' OR NOT EXISTS (
                  SELECT 1
                  FROM primary_person_media_credits collective_credit
                  INNER JOIN persons collective_person ON collective_person.id = collective_credit.person_id
                  WHERE collective_credit.media_asset_id = primary_credit.media_asset_id
                    AND collective_credit.role = primary_credit.role
                    AND collective_person.is_pseudonym = 1
                    AND collective_credit.person_id != primary_credit.person_id
              ))
            GROUP BY w.id, COALESCE(gp.id, p.id, w.id), w.collection_id, w.media_type, w.wikidata_qid,
                     c.display_name, primary_credit.role, primary_credit.association_type,
                     primary_credit.via_group_id, primary_credit.via_group_name,
                     primary_credit.membership_start_date, primary_credit.membership_end_date
            ORDER BY Year DESC, Title, primary_credit.role;
            """,
            new { personId })).ToList();

        baseRows.RemoveAll(row => row.IsThroughGroup && !MembershipOverlapsWork(row));

        var workQids = baseRows
            .SelectMany(row => new[] { row.WorkQid, row.RootWorkQid, row.CollectionQid })
            .Where(qid => !string.IsNullOrWhiteSpace(qid))
            .Select(qid => qid!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var charactersByWorkQid = new Dictionary<string, List<CharacterPortrayalDto>>(StringComparer.OrdinalIgnoreCase);
        if (workQids.Count > 0)
        {
            var characterRows = (await conn.QueryAsync<PersonCreditCharacterRow>(
                """
                SELECT cpl.work_qid           AS WorkQid,
                       fe.id                  AS CharacterId,
                       fe.label               AS CharacterName,
                       fe.wikidata_qid        AS CharacterQid,
                       fe.image_url           AS CharacterImageUrl,
                       cp.id                  AS PortraitId,
                       cp.image_url           AS PortraitImageUrl,
                       cp.local_image_path    AS PortraitLocalImagePath,
                       cp.is_default          AS PortraitIsDefault
                FROM character_performer_links cpl
                INNER JOIN fictional_entities fe
                    ON fe.id = cpl.fictional_entity_id
                LEFT JOIN character_portraits cp
                    ON cp.fictional_entity_id = fe.id
                   AND cp.person_id = @personId
                WHERE cpl.person_id = @personId
                  AND cpl.work_qid IN @workQids
                ORDER BY cpl.work_qid, fe.label, cp.is_default DESC;
                """,
                new { personId, workQids })).ToList();

            charactersByWorkQid = characterRows
                .GroupBy(row => row.WorkQid ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .GroupBy(row => new { row.CharacterId, row.CharacterName, row.CharacterQid })
                        .Select(characterGroup =>
                        {
                            var preferred = characterGroup
                                .OrderByDescending(row => row.PortraitIsDefault)
                                .ThenByDescending(row => !string.IsNullOrWhiteSpace(row.PortraitImageUrl))
                                .First();

                            return new CharacterPortrayalDto
                            {
                                FictionalEntityId = characterGroup.Key.CharacterId,
                                CharacterName = characterGroup.Key.CharacterName,
                                CharacterQid = characterGroup.Key.CharacterQid,
                                PortraitUrl = ApiImageUrls.BuildCharacterPortraitUrl(
                                    preferred.PortraitId,
                                    preferred.PortraitLocalImagePath,
                                    preferred.PortraitImageUrl),
                            };
                        })
                        .OrderBy(character => character.CharacterName, StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    StringComparer.OrdinalIgnoreCase);

            var person = await _personRepo.FindByIdAsync(personId, ct);
            if (person is not null)
            {
                var evidenceScopes = baseRows
                    .SelectMany(row => new[]
                    {
                        new CharacterEvidenceScope(row.WorkId, row.WorkQid),
                        new CharacterEvidenceScope(row.RootWorkId, row.RootWorkQid ?? row.WorkQid),
                    })
                    .Where(scope => !string.IsNullOrWhiteSpace(scope.WorkQid))
                    .Distinct()
                    .ToList();

                foreach (var scope in evidenceScopes)
                {
                    if (!charactersByWorkQid.TryGetValue(scope.WorkQid!, out var linkedCharacters)
                        || linkedCharacters.Count == 0)
                    {
                        continue;
                    }

                    var claimCredits = await BuildFallbackCreditsFromMetadataClaimsAsync(
                        scope.EntityId,
                        _personRepo,
                        _db,
                        ct);
                    var filteredCredit = new CastCreditDto
                    {
                        PersonId = person.Id,
                        Name = person.Name,
                        WikidataQid = person.WikidataQid,
                        Characters = linkedCharacters.ToList(),
                    };
                    await RemoveUncorroboratedOrdinalCharacterLinksAsync(
                        scope.EntityId,
                        [filteredCredit],
                        claimCredits,
                        _canonicalArrayRepo,
                        ct);
                    charactersByWorkQid[scope.WorkQid!] = filteredCredit.Characters;
                }
            }
        }

        return NormalizeLibraryCreditRows(baseRows, charactersByWorkQid);
    }

    private static List<PersonLibraryCreditDto> NormalizeLibraryCreditRows(
        IReadOnlyList<PersonLibraryCreditRow> baseRows,
        IReadOnlyDictionary<string, List<CharacterPortrayalDto>> charactersByWorkQid)
        => baseRows
            .GroupBy(LibraryCreditGroupKey)
            .SelectMany(group =>
            {
                var orderedRows = group
                    .OrderBy(row => row.Year, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(row => row.Title, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var directRows = orderedRows
                    .Where(row => !row.IsThroughGroup)
                    .ToList();
                var effectiveRows = directRows.Count > 0 ? directRows : orderedRows;
                var representative = effectiveRows.First();
                var isTvSeriesCredit = IsTvMediaType(representative.MediaType) && representative.CollectionId.HasValue;
                var isMusicAlbumCredit = IsMusicMediaType(representative.MediaType);
                var roles = representative.IsThroughGroup
                    ? [$"With {StringHelpers.FirstNonBlank(representative.ViaGroupName, "group")}"]
                    : ResolveLibraryCreditRoles(effectiveRows, charactersByWorkQid);
                var characterQids = effectiveRows
                    .SelectMany(row => new[] { row.CollectionQid, row.RootWorkQid, row.WorkQid })
                    .Where(qid => !string.IsNullOrWhiteSpace(qid))
                    .Select(qid => qid!)
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                var characters = characterQids
                    .SelectMany(qid => charactersByWorkQid.TryGetValue(qid, out var qidCharacters)
                        ? qidCharacters
                        : [])
                    .GroupBy(character => character.FictionalEntityId)
                    .Select(characterGroup => characterGroup.First())
                    .OrderBy(character => character.CharacterName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return roles.Select(role =>
                {
                    var includeCharacters = role.Equals("Actor", StringComparison.OrdinalIgnoreCase)
                        || role.Equals("Voice Actor", StringComparison.OrdinalIgnoreCase);

                    return new PersonLibraryCreditDto
                    {
                        WorkId = isMusicAlbumCredit ? representative.RootWorkId : representative.WorkId,
                        CollectionId = representative.CollectionId,
                        MediaType = representative.MediaType,
                        Title = isTvSeriesCredit
                            ? StringHelpers.FirstNonBlank(representative.CollectionTitle, representative.Title, "Untitled")!
                            : isMusicAlbumCredit
                                ? StringHelpers.FirstNonBlank(representative.RootTitle, representative.Title, "Untitled")!
                            : StringHelpers.FirstNonBlank(representative.Title, "Untitled")!,
                        CoverUrl = isTvSeriesCredit
                            ? representative.RootCoverAssetId is Guid rootCoverAssetId
                                ? $"/stream/artwork/{rootCoverAssetId:D}"
                                : null
                            : orderedRows.Select(row => row.FirstAssetId).FirstOrDefault(assetId => assetId.HasValue) is Guid assetId
                                ? $"/stream/{assetId}/cover-thumb"
                                : null,
                        Year = representative.Year,
                        Role = role,
                        AssociationType = representative.AssociationType,
                        ViaGroupId = representative.ViaGroupId,
                        ViaGroupName = representative.ViaGroupName,
                        AssociationIsInferred = representative.IsThroughGroup
                            && (!TryReadYear(representative.Year, out _)
                                || (!TryReadYear(representative.MembershipStartDate, out _)
                                    && !TryReadYear(representative.MembershipEndDate, out _))),
                        Characters = includeCharacters ? characters : [],
                    };
                });
            })
            .OrderByDescending(credit => credit.Year, StringComparer.OrdinalIgnoreCase)
            .ThenBy(credit => credit.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(credit => credit.Role, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string LibraryCreditGroupKey(PersonLibraryCreditRow row)
        => IsTvMediaType(row.MediaType) && row.CollectionId.HasValue
            ? $"tv::{row.CollectionId.Value:D}"
            : IsMusicMediaType(row.MediaType)
                ? $"music::{row.RootWorkId:D}"
            : $"work::{row.WorkId:D}";

    private static bool IsMusicMediaType(string? mediaType)
        => mediaType?.Contains("music", StringComparison.OrdinalIgnoreCase) == true;

    private static IReadOnlyList<string> ResolveLibraryCreditRoles(
        IReadOnlyList<PersonLibraryCreditRow> rows,
        IReadOnlyDictionary<string, List<CharacterPortrayalDto>> charactersByWorkQid)
    {
        var hasCharacterEvidence = rows
            .SelectMany(row => new[] { row.CollectionQid, row.WorkQid })
            .Where(qid => !string.IsNullOrWhiteSpace(qid))
            .Any(qid => charactersByWorkQid.ContainsKey(qid!));

        var roles = rows
            .Select(row => row.Role)
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Where(role => IsEligibleLibraryCreditRole(rows[0].MediaType, role))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(LibraryCreditRoleRank)
            .ThenBy(role => role, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (hasCharacterEvidence
            && !roles.Any(role => role.Equals("Actor", StringComparison.OrdinalIgnoreCase)
                || role.Equals("Voice Actor", StringComparison.OrdinalIgnoreCase)))
        {
            roles.Add("Actor");
            roles = roles
                .OrderBy(LibraryCreditRoleRank)
                .ThenBy(role => role, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return roles.Count > 0
            ? roles
            : rows.All(row => string.IsNullOrWhiteSpace(row.Role))
                ? ["Credit"]
                : [];
    }

    private static bool MembershipOverlapsWork(PersonLibraryCreditRow row)
    {
        if (!TryReadYear(row.Year, out var workYear))
        {
            return true;
        }

        if (TryReadYear(row.MembershipStartDate, out var startYear) && workYear < startYear)
        {
            return false;
        }

        return !TryReadYear(row.MembershipEndDate, out var endYear) || workYear <= endYear;
    }

    private static bool TryReadYear(string? value, out int year)
    {
        year = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var token = value.Trim();
        return token.Length >= 4 && int.TryParse(token[..4], out year);
    }

    private static bool IsEligibleLibraryCreditRole(string? mediaType, string role)
    {
        var normalizedRole = role.Trim().ToLowerInvariant();
        if (mediaType?.Contains("audiobook", StringComparison.OrdinalIgnoreCase) == true)
        {
            return normalizedRole is "author" or "narrator" or "performer";
        }

        if (IsMusicMediaType(mediaType))
        {
            return normalizedRole is "artist" or "performer" or "composer" or "producer";
        }

        return true;
    }

    private static int LibraryCreditRoleRank(string role)
        => role.Trim().ToLowerInvariant() switch
        {
            "actor" => 0,
            "voice actor" => 1,
            "author" => 2,
            "narrator" => 3,
            "director" => 4,
            "screenwriter" => 5,
            "writer" => 5,
            "producer" => 6,
            "composer" => 7,
            "artist" => 8,
            "performer" => 9,
            _ => 50,
        };

    private static bool IsTvMediaType(string? mediaType)
        => mediaType?.Contains("tv", StringComparison.OrdinalIgnoreCase) == true;

    public async Task<List<PersonCharacterRoleDto>> GetCharacterRolesAsync(
        Guid personId,
        CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        var rows = (await conn.QueryAsync<PersonCharacterRoleRow>(
            """
            SELECT cpl.fictional_entity_id  AS FictionalEntityId,
                   fe.label                 AS CharacterName,
                   fe.wikidata_qid          AS CharacterQid,
                   fe.image_url             AS CharacterImageUrl,
                   cp.id                    AS PortraitId,
                   cp.image_url             AS PortraitImageUrl,
                   cp.local_image_path      AS PortraitLocalImagePath,
                   cp.is_default            AS PortraitIsDefault,
                   w.id                     AS WorkId,
                   cpl.work_qid             AS WorkQid,
                   COALESCE(gp.id, p.id, w.id) AS RootWorkId,
                   COALESCE(
                       NULLIF(TRIM(COALESCE(gp.wikidata_qid, p.wikidata_qid, w.wikidata_qid)), ''),
                       (SELECT root_qid.value
                        FROM canonical_values root_qid
                        WHERE root_qid.entity_id = COALESCE(gp.id, p.id, w.id)
                          AND root_qid.key = 'wikidata_qid'
                        LIMIT 1)
                   )                        AS RootWorkQid,
                   w.collection_id          AS CollectionId,
                   w.media_type             AS MediaType,
                   COALESCE(MAX(CASE WHEN cv.key = 'title' THEN cv.value END), 'Untitled') AS WorkTitle,
                   fe.fictional_universe_qid AS UniverseQid,
                   fe.fictional_universe_label AS UniverseLabel
            FROM character_performer_links cpl
            INNER JOIN fictional_entities fe
                ON fe.id = cpl.fictional_entity_id
            INNER JOIN works w
                ON cpl.work_qid = COALESCE(
                    NULLIF(TRIM(w.wikidata_qid), ''),
                    (SELECT cvq.value
                     FROM canonical_values cvq
                     WHERE cvq.entity_id = w.id AND cvq.key = 'wikidata_qid'
                     LIMIT 1),
                    (SELECT cvq.value
                     FROM editions e
                     INNER JOIN media_assets ma ON ma.edition_id = e.id
                     INNER JOIN canonical_values cvq ON cvq.entity_id = ma.id
                     WHERE e.work_id = w.id AND cvq.key = 'wikidata_qid'
                     LIMIT 1),
                    (SELECT ij.resolved_qid
                     FROM editions e
                     INNER JOIN media_assets ma ON ma.edition_id = e.id
                     INNER JOIN identity_jobs ij ON ij.entity_id = ma.id
                     WHERE e.work_id = w.id
                       AND ij.resolved_qid IS NOT NULL
                       AND TRIM(ij.resolved_qid) <> ''
                     ORDER BY ij.updated_at DESC, ij.created_at DESC
                     LIMIT 1)
                )
            LEFT JOIN canonical_values cv
                ON cv.entity_id = w.id
               AND cv.key = 'title'
            LEFT JOIN works p
                ON p.id = w.parent_work_id
            LEFT JOIN works gp
                ON gp.id = p.parent_work_id
            LEFT JOIN character_portraits cp
                ON cp.fictional_entity_id = fe.id
               AND cp.person_id = cpl.person_id
            WHERE cpl.person_id = @personId
              AND cpl.work_qid IS NOT NULL
            GROUP BY cpl.fictional_entity_id, fe.label, fe.wikidata_qid, fe.image_url,
                     cp.id, cp.image_url, cp.local_image_path, cp.is_default,
                     w.id, cpl.work_qid, COALESCE(gp.id, p.id, w.id), RootWorkQid,
                     w.collection_id, w.media_type, fe.fictional_universe_qid, fe.fictional_universe_label
            ORDER BY UniverseLabel, WorkTitle, CharacterName, cp.is_default DESC;
            """,
            new { personId })).ToList();

        var allowedCharactersByWorkQid = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);
        var person = await _personRepo.FindByIdAsync(personId, ct);
        if (person is not null)
        {
            foreach (var qidGroup in rows
                         .Where(row => !string.IsNullOrWhiteSpace(row.WorkQid))
                         .GroupBy(row => row.WorkQid!, StringComparer.OrdinalIgnoreCase))
            {
                var filteredCredit = new CastCreditDto
                {
                    PersonId = person.Id,
                    Name = person.Name,
                    WikidataQid = person.WikidataQid,
                    Characters = qidGroup
                        .GroupBy(row => row.FictionalEntityId)
                        .Select(characterGroup =>
                        {
                            var character = characterGroup.First();
                            return new CharacterPortrayalDto
                            {
                                FictionalEntityId = character.FictionalEntityId,
                                CharacterName = character.CharacterName,
                                CharacterQid = character.CharacterQid,
                            };
                        })
                        .ToList(),
                };
                var evidenceScopes = qidGroup
                    .SelectMany(row => new[]
                    {
                        new CharacterEvidenceScope(row.WorkId, row.WorkQid),
                        new CharacterEvidenceScope(row.RootWorkId, row.RootWorkQid ?? row.WorkQid),
                    })
                    .Where(scope => string.Equals(scope.WorkQid, qidGroup.Key, StringComparison.OrdinalIgnoreCase))
                    .Distinct()
                    .ToList();

                foreach (var scope in evidenceScopes)
                {
                    var claimCredits = await BuildFallbackCreditsFromMetadataClaimsAsync(
                        scope.EntityId,
                        _personRepo,
                        _db,
                        ct);
                    await RemoveUncorroboratedOrdinalCharacterLinksAsync(
                        scope.EntityId,
                        [filteredCredit],
                        claimCredits,
                        _canonicalArrayRepo,
                        ct);
                }

                allowedCharactersByWorkQid[qidGroup.Key] = filteredCredit.Characters
                    .Select(character => character.FictionalEntityId)
                    .ToHashSet();
            }
        }

        return rows
            .Where(row => string.IsNullOrWhiteSpace(row.WorkQid)
                || !allowedCharactersByWorkQid.TryGetValue(row.WorkQid, out var allowedCharacters)
                || allowedCharacters.Contains(row.FictionalEntityId))
            .GroupBy(row => new { row.WorkId, row.FictionalEntityId })
            .Select(group =>
            {
                var preferred = group
                    .OrderByDescending(row => row.PortraitIsDefault)
                    .ThenByDescending(row => !string.IsNullOrWhiteSpace(row.PortraitImageUrl))
                    .First();

                return new PersonCharacterRoleDto
                {
                    FictionalEntityId = group.Key.FictionalEntityId,
                    CharacterName = preferred.CharacterName,
                    PortraitUrl = ApiImageUrls.BuildCharacterPortraitUrl(
                        preferred.PortraitId,
                        preferred.PortraitLocalImagePath,
                        preferred.PortraitImageUrl),
                    WorkId = preferred.WorkId,
                    WorkQid = preferred.WorkQid,
                    WorkTitle = preferred.WorkTitle,
                    CollectionId = preferred.CollectionId,
                    MediaType = preferred.MediaType,
                    IsDefault = preferred.PortraitIsDefault,
                    UniverseQid = preferred.UniverseQid,
                    UniverseLabel = preferred.UniverseLabel,
                };
            })
            .OrderBy(role => role.UniverseLabel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(role => role.WorkTitle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(role => role.CharacterName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private sealed class PersonGroupMemberRow
    {
        public Guid Id { get; init; }
        public string? Name { get; init; }
        public string? DateRange { get; init; }
        public string? HeadshotUrl { get; init; }
        public string? LocalHeadshotPath { get; init; }
    }

    private sealed class PersonLibraryCreditRow
    {
        public Guid WorkId { get; init; }
        public Guid RootWorkId { get; init; }
        public Guid? CollectionId { get; init; }
        public string? MediaType { get; init; }
        public string? CollectionTitle { get; init; }
        public string? CollectionQid { get; init; }
        public string? WorkQid { get; init; }
        public string? RootWorkQid { get; init; }
        public string? Title { get; init; }
        public string? RootTitle { get; init; }
        public string? Year { get; init; }
        public string Role { get; init; } = string.Empty;
        public string AssociationType { get; init; } = "Direct";
        public Guid? ViaGroupId { get; init; }
        public string? ViaGroupName { get; init; }
        public string? MembershipStartDate { get; init; }
        public string? MembershipEndDate { get; init; }
        public bool IsThroughGroup => AssociationType.Equals("ThroughGroup", StringComparison.OrdinalIgnoreCase);
        public Guid? RootCoverAssetId { get; init; }
        public Guid? FirstAssetId { get; init; }
    }

    private sealed record CharacterEvidenceScope(Guid EntityId, string? WorkQid);

    private sealed class PersonCreditCharacterRow
    {
        public string? WorkQid { get; init; }
        public Guid CharacterId { get; init; }
        public string? CharacterName { get; init; }
        public string? CharacterQid { get; init; }
        public string? CharacterImageUrl { get; init; }
        public Guid? PortraitId { get; init; }
        public string? PortraitImageUrl { get; init; }
        public string? PortraitLocalImagePath { get; init; }
        public bool PortraitIsDefault { get; init; }
    }

    private sealed class PersonCharacterRoleRow
    {
        public Guid FictionalEntityId { get; init; }
        public string? CharacterName { get; init; }
        public string? CharacterQid { get; init; }
        public string? CharacterImageUrl { get; init; }
        public Guid? PortraitId { get; init; }
        public string? PortraitImageUrl { get; init; }
        public string? PortraitLocalImagePath { get; init; }
        public bool PortraitIsDefault { get; init; }
        public Guid WorkId { get; init; }
        public string? WorkQid { get; init; }
        public Guid RootWorkId { get; init; }
        public string? RootWorkQid { get; init; }
        public Guid? CollectionId { get; init; }
        public string? MediaType { get; init; }
        public string? WorkTitle { get; init; }
        public string? UniverseQid { get; init; }
        public string? UniverseLabel { get; init; }
    }
}
