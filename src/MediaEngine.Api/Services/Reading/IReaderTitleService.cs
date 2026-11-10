namespace MediaEngine.Api.Services.Reading;

/// <summary>The words the page reader shows in its top line: the title and one line under it.</summary>
public sealed record ReaderTitle(string? Title, string? Subtitle, string? Series = null);

public interface IReaderTitleService
{
    Task<ReaderTitle> GetAsync(Guid assetId, bool isComic, CancellationToken ct);
}
