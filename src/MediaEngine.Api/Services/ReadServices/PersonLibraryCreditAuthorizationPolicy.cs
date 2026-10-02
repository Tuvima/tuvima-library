using MediaEngine.Api.Services.Display;
using MediaEngine.Contracts.Persons;

namespace MediaEngine.Api.Services.ReadServices;

/// <summary>
/// Applies catalogue visibility to person credits without changing the
/// structural artwork identity selected by <see cref="PersonCreditReadService"/>.
/// </summary>
internal static class PersonLibraryCreditAuthorizationPolicy
{
    public static List<PersonLibraryCreditDto> Filter(
        IEnumerable<PersonLibraryCreditDto> credits,
        IReadOnlyList<DisplayWorkRow> visibleWorks)
    {
        var visibleByWork = visibleWorks
            .GroupBy(work => work.WorkId)
            .ToDictionary(group => group.Key, group => group.First());

        var result = new List<PersonLibraryCreditDto>();
        foreach (var credit in credits)
        {
            var authorizedWork = IsMusicCredit(credit) && credit.SourceWorkIds.Count > 0
                ? credit.SourceWorkIds.FirstOrDefault(visibleByWork.ContainsKey)
                : credit.WorkId;
            if (authorizedWork == Guid.Empty || !visibleByWork.TryGetValue(authorizedWork, out var visibleWork))
            {
                continue;
            }

            result.Add(Project(credit, visibleWork));
        }

        return result;
    }

    private static bool IsMusicCredit(PersonLibraryCreditDto credit)
        => credit.MediaType?.Contains("music", StringComparison.OrdinalIgnoreCase) == true;

    private static PersonLibraryCreditDto Project(
        PersonLibraryCreditDto credit,
        DisplayWorkRow visibleWork)
    {
        var isTvShow = credit.CollectionId.HasValue
            && credit.MediaType?.Contains("tv", StringComparison.OrdinalIgnoreCase) == true;

        return new PersonLibraryCreditDto
        {
            WorkId = credit.WorkId,
            SourceWorkIds = credit.SourceWorkIds,
            CollectionId = credit.CollectionId,
            MediaType = credit.MediaType,
            Title = credit.Title,
            // A person credit for TV represents the show, even though the owned
            // work authorizing it is an episode. Never replace the show's poster
            // with that episode's still.
            CoverUrl = isTvShow ? credit.CoverUrl : visibleWork.CoverUrl,
            Year = credit.Year,
            Role = credit.Role,
            AssociationType = credit.AssociationType,
            ViaGroupId = credit.ViaGroupId,
            ViaGroupName = credit.ViaGroupName,
            AssociationIsInferred = credit.AssociationIsInferred,
            Characters = credit.Characters,
        };
    }
}
