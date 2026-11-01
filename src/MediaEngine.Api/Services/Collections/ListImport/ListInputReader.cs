using MediaEngine.Api.Services.Collections.ListImport.Sources;

namespace MediaEngine.Api.Services.Collections.ListImport;

/// <summary>
/// The one place a list a person brought is read into entries, whether it is a file, pasted titles or a pasted web address
/// (Collections and lists spec 4b, 4d). An address is looked up among the web sources; everything else is read by
/// <see cref="ListFileParser"/>. The preview, the save and the refresh all read through here, so they cannot disagree.
/// </summary>
public sealed class ListInputReader(WebListResolver web)
{
    /// <exception cref="ListFileNotUnderstoodException">The input is not a list (or an address that cannot be used).</exception>
    public async Task<ParsedList> ReadAsync(string fileName, byte[] bytes, CancellationToken ct)
    {
        if (await web.ResolveAsync(bytes, ct).ConfigureAwait(false) is { } fromWeb)
        {
            return fromWeb;
        }

        return ListFileParser.Parse(fileName, bytes);
    }
}
