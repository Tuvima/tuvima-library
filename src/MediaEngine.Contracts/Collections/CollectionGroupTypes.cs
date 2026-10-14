namespace MediaEngine.Contracts.Collections;

/// <summary>
/// The user-facing kind of a group, as native clients should name and route it. Internal collection
/// identifiers may back any of these; the kind decides the label: a TV show is a show, a book or movie
/// sequence is a series, a rollup across shelves is a collection, and a hand-picked or system list is a list.
/// A universe is the broader franchise rollup served by <c>/api/v1/universe/{qid}</c>.
/// </summary>
public static class CollectionGroupTypes
{
    public const string Show = "show";
    public const string Series = "series";
    public const string Collection = "collection";
    public const string List = "list";
    public const string Universe = "universe";
}
