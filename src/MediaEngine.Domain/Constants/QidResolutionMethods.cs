namespace MediaEngine.Domain.Constants;

/// <summary>
/// Values stored in the <c>qid_resolution_method</c> claim when the Wikidata match was made in a way
/// worth naming. Other methods (<c>bridge_id</c>, <c>retail_text</c>, <c>sibling_variant</c>, the roll-up
/// methods) are still written inline by the Wikidata bridge worker.
/// </summary>
public static class QidResolutionMethods
{
    /// <summary>
    /// An audiobook with no retail match took the Wikidata ID of an owned, confirmed book with the same
    /// title and author. Nothing was looked up outside the machine to find it.
    /// </summary>
    public const string OwnedBookNoRetail = "owned_book_no_retail";
}
