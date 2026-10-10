namespace MediaEngine.Domain.Contracts;

/// <summary>What happens to a removed person's personal photos and videos.</summary>
public enum ProfilePhotoDisposition
{
    /// <summary>Kept in the household's Shared Library, in a folder named "From &lt;name&gt; &lt;date&gt;".</summary>
    MoveToShared = 0,

    /// <summary>Deleted with the person.</summary>
    Delete = 1,
}
