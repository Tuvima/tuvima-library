using System.Text.RegularExpressions;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Services.Plugins.Declarative;

internal sealed record DeclarativeAiStepResult(PluginAiStatus Status, string Message, string? Text);

/// <summary>
/// Runs an AI step a JSON-only plugin declared: fills the prompt with the inputs, asks Local AI as that plugin through the
/// plugin door, and returns the answer or a plain status. The plugin has to be switched on and approved first.
/// </summary>
internal sealed partial class DeclarativeAiStepRunner(PluginCatalog catalog, PluginAiGateway gateway)
{
    private const int MaxInputLength = 12000;

    public async Task<DeclarativeAiStepResult?> RunAsync(
        string pluginId,
        string stepKey,
        IReadOnlyDictionary<string, string> inputs,
        CancellationToken cancellationToken)
    {
        var registration = catalog.Get(pluginId);
        var step = registration?.Capabilities.OfType<DeclarativeAiStepCapability>()
            .Select(capability => capability.Step)
            .FirstOrDefault(candidate => string.Equals(candidate.Key, stepKey, StringComparison.Ordinal));
        if (registration is null || step is null)
        {
            return null;
        }

        if (!registration.Enabled || registration.LoadError is not null)
        {
            return new DeclarativeAiStepResult(PluginAiStatus.Refused, "This plugin is switched off.", null);
        }

        var prompt = Fill(step.Prompt, inputs, out var missing);
        if (missing is not null)
        {
            return new DeclarativeAiStepResult(PluginAiStatus.Failed, $"The step needs an input called \"{missing}\".", null);
        }

        var options = new PluginAiOptions { MaxTokens = step.MaxTokens };
        var result = step.JsonGrammar is null
            ? await gateway.InferTextAsync(pluginId, step.Role, prompt, options, cancellationToken).ConfigureAwait(false)
            : await gateway.InferJsonAsync(pluginId, step.Role, prompt, step.JsonGrammar, options, cancellationToken).ConfigureAwait(false);
        return new DeclarativeAiStepResult(result.Status, result.Message, result.Text);
    }

    /// <summary>Replaces each <c>{name}</c> with its input; <paramref name="missing"/> names the first placeholder with no input.</summary>
    internal static string Fill(string template, IReadOnlyDictionary<string, string> inputs, out string? missing)
    {
        string? firstMissing = null;
        var filled = Placeholder().Replace(template, match =>
        {
            var name = match.Groups["name"].Value;
            if (!inputs.TryGetValue(name, out var value))
            {
                firstMissing ??= name;
                return match.Value;
            }

            return value.Length > MaxInputLength ? value[..MaxInputLength] : value;
        });
        missing = firstMissing;
        return filled;
    }

    [GeneratedRegex(@"\{(?<name>[a-z][a-z0-9_]{0,31})\}")]
    private static partial Regex Placeholder();
}
