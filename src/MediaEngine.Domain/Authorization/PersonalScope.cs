namespace MediaEngine.Domain.Authorization;

/// <summary>The personal pages that can show more than the signed-in person's own data to an administrator.</summary>
public enum PersonalScopeKind
{
    Activity,
    Taste,
}

public enum PersonalScopeTarget
{
    /// <summary>Only the person using the page.</summary>
    Self,

    /// <summary>One profile.</summary>
    Profile,

    /// <summary>Every profile in one household.</summary>
    Household,

    /// <summary>Every profile on the server.</summary>
    Everyone,
}

/// <summary>Whose personal data a page asks to show: Me, one profile, a household or everyone.</summary>
public readonly record struct PersonalScope
{
    private PersonalScope(PersonalScopeTarget target, Guid? id)
    {
        Target = target;
        Id = id;
    }

    public PersonalScopeTarget Target { get; }

    /// <summary>The profile or household id; <see langword="null"/> for <see cref="PersonalScopeTarget.Self"/> and <see cref="PersonalScopeTarget.Everyone"/>.</summary>
    public Guid? Id { get; }

    public static PersonalScope Self { get; } = new(PersonalScopeTarget.Self, null);
    public static PersonalScope Everyone { get; } = new(PersonalScopeTarget.Everyone, null);
    public static PersonalScope Profile(Guid profileId) => new(PersonalScopeTarget.Profile, profileId);
    public static PersonalScope Household(Guid householdId) => new(PersonalScopeTarget.Household, householdId);
}

public enum PersonalScopeRefusal
{
    None,

    /// <summary>No signed-in person is acting (an app, or no active profile).</summary>
    NotSignedIn,

    /// <summary>The role does not allow this scope.</summary>
    NotAllowed,

    /// <summary>An administrator who turned on their PIN has not unlocked it.</summary>
    UnlockRequired,

    /// <summary>The profile or household does not exist.</summary>
    UnknownTarget,
}

/// <summary>
/// The answer to "may this person see that scope?". When allowed, <see cref="ProfileIds"/> is the set of profiles the
/// page may read, or <see cref="AllProfiles"/> is true for Everyone (<see cref="ProfileIds"/> is then empty).
/// </summary>
public sealed record PersonalScopeDecision
{
    private PersonalScopeDecision(
        bool allowed,
        PersonalScopeRefusal refusal,
        IReadOnlySet<Guid> profileIds,
        bool allProfiles,
        bool includesOthers)
    {
        Allowed = allowed;
        Refusal = refusal;
        ProfileIds = profileIds;
        AllProfiles = allProfiles;
        IncludesOthers = includesOthers;
    }

    public bool Allowed { get; }
    public PersonalScopeRefusal Refusal { get; }
    public IReadOnlySet<Guid> ProfileIds { get; }
    public bool AllProfiles { get; }

    /// <summary>True when the scope covers anyone besides the person acting; viewing it is recorded in the audit log.</summary>
    public bool IncludesOthers { get; }

    public static PersonalScopeDecision Allow(IReadOnlySet<Guid> profileIds, bool includesOthers) =>
        new(true, PersonalScopeRefusal.None, profileIds, false, includesOthers);

    public static PersonalScopeDecision AllowEveryone() =>
        new(true, PersonalScopeRefusal.None, new HashSet<Guid>(), true, true);

    public static PersonalScopeDecision Refuse(PersonalScopeRefusal reason) =>
        reason == PersonalScopeRefusal.None
            ? throw new ArgumentException("A refusal reason is required.", nameof(reason))
            : new(false, reason, new HashSet<Guid>(), false, false);
}
