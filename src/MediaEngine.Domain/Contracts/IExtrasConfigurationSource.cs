using MediaEngine.Domain.Configuration;

namespace MediaEngine.Domain.Contracts;

/// <summary>
/// The supporting-files rules in force (<c>config/extras.json</c>, universal editor v5 D34), as the places that only hold Domain
/// types need them: the repository checks a role against the list for the file's kind, and the cards read the role labels.
/// </summary>
public interface IExtrasConfigurationSource
{
    /// <summary>The rules in force, read again when the config file changed.</summary>
    ExtrasConfiguration Current { get; }
}
