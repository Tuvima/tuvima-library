using MediaEngine.Domain.Configuration;

namespace MediaEngine.Domain.Contracts;

/// <summary>
/// The editor's numbers and per-kind rules in force (<c>config/editor.json</c>, universal editor v5 D34), as the Engine reads that build the
/// editor's header need them: the inline threshold, page sizes, and the tabs, facts and match fields of each kind of item.
/// </summary>
public interface IEditorConfigurationSource
{
    /// <summary>The rules in force, read again when the config file changed.</summary>
    EditorConfiguration Current { get; }
}
