namespace MediaEngine.Domain.Contracts;

/// <summary>What the Extras scan learns about a file beyond its size: how long a clip is, how many pages a document has.</summary>
public sealed record ItemExtraFacts(double? DurationSeconds, int? PageCount);

/// <summary>Reads the facts shown on an Extras card. A file it cannot read gives no facts; it never throws for a bad file.</summary>
public interface IItemExtraFactsReader
{
    Task<ItemExtraFacts> ReadAsync(string filePath, string kind, CancellationToken ct = default);
}
