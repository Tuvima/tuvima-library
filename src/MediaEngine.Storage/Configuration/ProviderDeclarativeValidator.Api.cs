using System.Text.RegularExpressions;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Storage.Configuration;

internal static partial class ProviderDeclarativeValidator
{
    private static readonly string[] CredentialNames = ["api_key", "pin", "username", "password", "client_key", "access_token"];

    private static void ValidateApiAndFlows(ProviderConfiguration provider, List<string> errors)
    {
        if (provider.RegistrationOrder < 0)
        {
            errors.Add("registration_order must not be negative.");
        }

        if (provider.Api is { } api)
        {
            ValidateApi(api, errors);
        }

        if (provider.Auth is { } auth)
        {
            ValidateAuth(auth, provider.Api, errors);
        }

        if (provider.Flows is { } flows)
        {
            AddUnknown(flows.UnmappedProperties, "flows", errors);
            if (flows.Fetch is { } fetch)
            {
                ValidateFlow(fetch, "flows.fetch", provider, errors);
            }

            if (flows.Search is { } search)
            {
                ValidateFlow(search, "flows.search", provider, errors);
            }
        }
    }

    // -- auth -----------------------------------------------------------------------------------

    private static void ValidateAuth(ProviderAuthConfiguration auth, ProviderApiConfiguration? api, List<string> errors)
    {
        AddUnknown(auth.UnmappedProperties, "auth", errors);
        var login = auth.Login;
        AddUnknown(login.UnmappedProperties, "auth.login", errors);
        if (!string.Equals(login.Method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("auth.login.method must be POST.");
        }

        if (!login.Path.StartsWith('/'))
        {
            errors.Add("auth.login.path must start with '/'.");
        }

        if (login.Body.Count == 0)
        {
            errors.Add("auth.login.body must list at least one property.");
        }

        foreach (var (key, template) in login.Body)
        {
            foreach (Match match in Regex.Matches(template, @"\{([a-z_]+)\??\}"))
            {
                if (!CredentialNames.Contains(match.Groups[1].Value))
                {
                    errors.Add($"auth.login.body['{key}'] uses {{{match.Groups[1].Value}}}, which is not a credential ({string.Join(", ", CredentialNames)}).");
                }
            }
        }

        AddRequired(errors, login.TokenPath, "auth.login.token_path");
        if (login.LifetimeDays <= 0)
        {
            errors.Add("auth.login.lifetime_days must be greater than 0.");
        }

        if (login.RefreshEarlyMinutes < 0 || login.RefreshEarlyMinutes >= login.LifetimeDays * 24 * 60)
        {
            errors.Add("auth.login.refresh_early_minutes must be at least 0 and shorter than the token lifetime.");
        }

        AddUnknown(auth.Deliver.UnmappedProperties, "auth.deliver", errors);
        AddRequired(errors, auth.Deliver.Header, "auth.deliver.header");
        if (!auth.Deliver.Format.Contains("{token}", StringComparison.Ordinal))
        {
            errors.Add("auth.deliver.format must contain {token}.");
        }

        if (auth.RetryOnUnauthorized is < 0 or > 1)
        {
            errors.Add("auth.retry_on_unauthorized must be 0 or 1.");
        }

        if (api is null || api.AllowedHosts.Count == 0)
        {
            errors.Add("auth needs api.allowed_hosts, so the sign-in only reaches known hosts.");
        }
    }

    // -- api ------------------------------------------------------------------------------------

    private static void ValidateApi(ProviderApiConfiguration api, List<string> errors)
    {
        AddUnknown(api.UnmappedProperties, "api", errors);
        if (api.AllowedHosts.Count == 0 || api.AllowedHosts.Any(string.IsNullOrWhiteSpace))
        {
            errors.Add("api.allowed_hosts must list at least one host.");
        }

        if (api.CacheMinutes < 0 || api.CacheMaxEntries < 1)
        {
            errors.Add("api.cache_minutes must not be negative and api.cache_max_entries must be at least 1.");
        }

        foreach (var (name, endpoint) in api.Endpoints)
        {
            var path = $"api.endpoints['{name}']";
            AddUnknown(endpoint.UnmappedProperties, path, errors);
            if (!VariableName.IsMatch(name))
            {
                errors.Add($"{path} must have a lower-case name.");
            }

            if (!endpoint.Path.StartsWith('/'))
            {
                errors.Add($"{path}.path must start with '/'.");
            }

            foreach (var (argument, kind) in endpoint.Args.Where(pair => !ApiArgumentKinds.IsKnown(pair.Value)))
            {
                errors.Add($"{path}.args['{argument}'] kind '{kind}' must be digits, alpha3, text or one_of:a,b (optionally followed by ?).");
            }

            foreach (var key in endpoint.Defaults.Keys.Where(key => !endpoint.Args.ContainsKey(key)))
            {
                errors.Add($"{path}.defaults names unknown argument '{key}'.");
            }

            foreach (Match match in Regex.Matches(endpoint.Path, @"\{([a-z_][a-z0-9_]*)\}"))
            {
                var placeholder = match.Groups[1].Value;
                if (!endpoint.Args.ContainsKey(placeholder) && !(placeholder == "page" && endpoint.Paging is not null))
                {
                    errors.Add($"{path}.path uses {{{placeholder}}}, which is not an argument.");
                }
            }

            if (endpoint.CacheMinutes is < 0)
            {
                errors.Add($"{path}.cache_minutes must not be negative.");
            }

            var pageUsed = endpoint.Path.Contains("{page}", StringComparison.Ordinal);
            if (endpoint.Paging is not { } paging)
            {
                if (pageUsed)
                {
                    errors.Add($"{path}.path uses {{page}} but the endpoint has no paging.");
                }

                continue;
            }

            AddUnknown(paging.UnmappedProperties, $"{path}.paging", errors);
            if (!pageUsed)
            {
                errors.Add($"{path}.paging needs {{page}} in the path.");
            }

            if (paging.MaxPages < 1 || paging.Start < 0)
            {
                errors.Add($"{path}.paging needs max_pages of at least 1 and a start that is not negative.");
            }

            AddRequired(errors, paging.ItemsPath, $"{path}.paging.items_path");
            AddRequired(errors, paging.NextPath, $"{path}.paging.next_path");
        }
    }

    // -- flows ----------------------------------------------------------------------------------

    private static void ValidateFlow(ProviderFlowConfiguration flow, string path, ProviderConfiguration provider, List<string> errors)
    {
        AddUnknown(flow.UnmappedProperties, path, errors);
        ValidateMediaTypes(flow.MediaTypes, $"{path}.media_types", errors);
        foreach (var entity in flow.EntityTypes.Where(name => !Enum.TryParse<EntityType>(name, true, out _)))
        {
            errors.Add($"{path}.entity_types contains unknown entity type '{entity}'.");
        }

        if (flow.Steps.Count == 0)
        {
            errors.Add($"{path}.steps must contain at least one step.");
        }

        if (flow.RequiresCredentials && provider.Auth is null && provider.Api is null)
        {
            errors.Add($"{path}.requires_credentials needs an api or auth block.");
        }

        var problems = new List<string>();
        var program = FlowParser.Parse(flow.Steps, $"{path}.steps", problems);
        errors.AddRange(problems);

        var builtIn = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (variable, location) in program.Reads.Where(read => !program.Assigned.Contains(read.Variable) && !builtIn.Contains(read.Variable)))
        {
            errors.Add($"{location} reads ${variable}, which no step sets.");
        }

        var endpoints = provider.Api?.Endpoints;
        foreach (var (endpoint, arguments, location) in program.Calls)
        {
            if (endpoints is null || !endpoints.TryGetValue(endpoint, out var declared))
            {
                errors.Add($"{location} calls endpoint '{endpoint}', which the provider's api does not declare.");
                continue;
            }

            foreach (var argument in arguments.Where(argument => !declared.Args.ContainsKey(argument)))
            {
                errors.Add($"{location} gives argument '{argument}', which endpoint '{endpoint}' does not declare.");
            }

            foreach (var (argument, kind) in declared.Args.Where(pair =>
                !pair.Value.EndsWith('?') && !declared.Defaults.ContainsKey(pair.Key) && !arguments.Contains(pair.Key)))
            {
                errors.Add($"{location} does not give the required argument '{argument}' of endpoint '{endpoint}'.");
            }
        }

        foreach (var step in Descendants(program.Steps).OfType<ClaimsStep>())
        {
            ValidateAddMode(step.Add, $"{step.Location}.claims.add", errors);
            for (var index = 0; index < step.Rules.Count; index++)
            {
                ValidateRule(
                    step.Rules[index], $"{step.Location}.claims.rules[{index}]", new HashSet<string>(StringComparer.Ordinal), 1,
                    new Scope(false), errors);
            }
        }
    }

    private static IEnumerable<FlowStep> Descendants(IEnumerable<FlowStep> steps)
    {
        foreach (var step in steps)
        {
            yield return step;
            var children = step switch
            {
                IfStep conditional => conditional.Then.Concat(conditional.Else),
                EachStep each => each.Body,
                _ => [],
            };
            foreach (var child in Descendants(children))
            {
                yield return child;
            }
        }
    }
}
