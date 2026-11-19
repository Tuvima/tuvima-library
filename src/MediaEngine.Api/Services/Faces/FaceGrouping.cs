namespace MediaEngine.Api.Services.Faces;

/// <summary>Works out which person a new face looks like, by comparing its numbers with the people already found (spec view-people-search WP7).</summary>
public sealed class FaceIndex
{
    /// <summary>
    /// How alike two looks must be (cosine, 1 = identical) to be called the same person. 0.363 is the value the SFace authors give for
    /// "same identity"; it is checked on real photos in the Local check and can be tuned in one place.
    /// </summary>
    public const double SameFaceThreshold = 0.363;

    private readonly Dictionary<Guid, (float[] Sum, int Count)> _people = [];

    /// <summary>Scales a look to length 1, so a comparison is a plain dot product. Returns a new array.</summary>
    public static float[] Normalize(float[] look)
    {
        var length = Math.Sqrt(look.Sum(value => (double)value * value));
        return length < 1e-9 ? [.. look] : look.Select(value => (float)(value / length)).ToArray();
    }

    public static double Similarity(float[] a, float[] b)
    {
        double dot = 0;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            dot += a[i] * b[i];
        }

        return dot;
    }

    /// <summary>Adds a look (already normalised) to a person.</summary>
    public void Add(Guid personId, float[] look)
    {
        if (!_people.TryGetValue(personId, out var entry))
        {
            _people[personId] = ([.. look], 1);
            return;
        }

        for (var i = 0; i < look.Length && i < entry.Sum.Length; i++)
        {
            entry.Sum[i] += look[i];
        }

        _people[personId] = (entry.Sum, entry.Count + 1);
    }

    /// <summary>The person this look is closest to, or null when nobody is alike enough. Skips people it must not go with.</summary>
    public Guid? Closest(float[] look, ISet<Guid>? excluded = null)
    {
        Guid? best = null;
        var bestScore = SameFaceThreshold;
        foreach (var (personId, entry) in _people)
        {
            if (excluded?.Contains(personId) == true)
            {
                continue;
            }

            var score = Similarity(Normalize(entry.Sum), look);
            if (score >= bestScore)
            {
                best = personId;
                bestScore = score;
            }
        }

        return best;
    }
}
