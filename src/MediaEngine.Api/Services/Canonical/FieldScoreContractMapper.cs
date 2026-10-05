using MediaEngine.Contracts.Search;
using MediaEngine.Domain.Models;
namespace MediaEngine.Api.Services.Canonical;

internal static class FieldScoreContractMapper
{
    internal static List<FieldScoreDto>? Map(IReadOnlyList<RetailFieldScore>? rows) => rows?.Select(row => new FieldScoreDto
    {
        Key = row.Key, Label = row.Label, Score = row.Score, Weight = row.Weight, Missing = row.Missing,
        Role = row.Role, Contribution = row.Contribution, MissingPolicy = row.MissingPolicy,
        Verdict = row.Verdict, LocalValue = row.LocalValue, CandidateValue = row.CandidateValue,
    }).ToList();
}
