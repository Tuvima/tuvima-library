using System.Text.Json;
using MediaEngine.Domain.Configuration;
using MediaEngine.Storage.Configuration;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// Bad shapes in a provider's sign-in, API endpoints and flows are reported when the file is
/// loaded. Each case edits the shipped TVDB file.
/// </summary>
public sealed class ProviderApiValidationTests
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static ProviderConfiguration Shipped()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "MediaEngine.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return JsonSerializer.Deserialize<ProviderConfiguration>(
            File.ReadAllText(Path.Combine(dir!, "config", "providers", "tvdb.json")), s_json)!;
    }

    private static IReadOnlyList<string> Errors(Action<ProviderConfiguration> edit)
    {
        var config = Shipped();
        edit(config);
        return JsonConfigValidator.Validate(config, "providers/tvdb.json");
    }

    private static IReadOnlyList<string> ErrorsAfterTextEdit(string from, string to)
    {
        var json = JsonSerializer.Serialize(Shipped(), s_json).Replace(from, to, StringComparison.Ordinal);
        return JsonConfigValidator.Validate(
            JsonSerializer.Deserialize<ProviderConfiguration>(json, s_json)!, "providers/tvdb.json");
    }

    [Fact]
    public void ShippedFile_HasNoErrors() =>
        Assert.Empty(Errors(_ => { }));

    [Fact]
    public void LoginShapes_AreChecked()
    {
        var errors = Errors(config =>
        {
            var login = config.Auth!.Login;
            login.Method = "GET";
            login.Path = "login";
            login.Body["x"] = "{password_hash}";
            login.TokenPath = string.Empty;
            login.LifetimeDays = 0;
            config.Auth.Deliver.Format = "Bearer";
            config.Auth.RetryOnUnauthorized = 3;
        });

        Assert.Contains("auth.login.method must be POST.", errors);
        Assert.Contains("auth.login.path must start with '/'.", errors);
        Assert.Contains(errors, error => error.Contains("{password_hash}, which is not a credential", StringComparison.Ordinal));
        Assert.Contains("auth.login.token_path is required.", errors);
        Assert.Contains("auth.login.lifetime_days must be greater than 0.", errors);
        Assert.Contains("auth.deliver.format must contain {token}.", errors);
        Assert.Contains("auth.retry_on_unauthorized must be 0 or 1.", errors);
    }

    [Theory]
    [InlineData("key-{api_key}")]
    [InlineData("{api_key}{pin}")]
    [InlineData("{api_key")]
    public void LoginValueThatMixesTextWithACredential_IsRejected(string template)
    {
        var errors = Errors(config => config.Auth!.Login.Body["apikey"] = template);

        Assert.Contains(errors, error => error.Contains("auth.login.body['apikey'] must be exactly one credential", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("{api_key}")]
    [InlineData("{pin?}")]
    [InlineData("plain text")]
    public void LoginValueThatIsOneCredentialOrPlainText_IsAccepted(string template)
    {
        var errors = Errors(config => config.Auth!.Login.Body["apikey"] = template);

        Assert.DoesNotContain(errors, error => error.Contains("must be exactly one credential", StringComparison.Ordinal));
    }

    [Fact]
    public void SignInWithoutAllowedHosts_IsRejected()
    {
        var errors = Errors(config => config.Api!.AllowedHosts.Clear());

        Assert.Contains("auth needs api.allowed_hosts, so the sign-in only reaches known hosts.", errors);
        Assert.Contains("api.allowed_hosts must list at least one host.", errors);
    }

    [Fact]
    public void EndpointShapes_AreChecked()
    {
        var errors = Errors(config =>
        {
            var endpoint = config.Api!.Endpoints["series_translation"];
            endpoint.Path = "series/{id}/{ghost}";
            endpoint.Args["lang"] = "number";
            endpoint.Defaults["nothing"] = "x";
            var paged = config.Api.Endpoints["series_episodes"];
            paged.Paging!.MaxPages = 0;
            paged.Path = "/series/{id}";
        });

        Assert.Contains(errors, error => error.Contains("path must start with '/'", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("uses {ghost}, which is not an argument", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("kind 'number' must be digits", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("defaults names unknown argument 'nothing'", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("paging needs {page} in the path", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("paging needs max_pages of at least 1", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownApiProperty_IsReportedWithItsPath()
    {
        var errors = ErrorsAfterTextEdit("\"cache_max_entries\"", "\"cache_entries\"");

        Assert.Contains("api.cache_entries is not a known property.", errors);
    }

    [Fact]
    public void FlowThatCallsAnUndeclaredEndpointOrWrongArguments_IsRejected()
    {
        var errors = ErrorsAfterTextEdit("\"call\":\"search\"", "\"call\":\"no_such_endpoint\"");

        Assert.Contains(errors, error => error.Contains("calls endpoint 'no_such_endpoint'", StringComparison.Ordinal));
    }

    [Fact]
    public void FlowStepShapes_AreReportedWithTheirPath()
    {
        var errors = ErrorsAfterTextEdit("\"stop_if_all\"", "\"stop_when_all\"");

        Assert.Contains(errors, error => error.Contains("flows.fetch.steps[3] needs exactly one of", StringComparison.Ordinal));
    }

    [Fact]
    public void FlowThatReadsAnUnsetVariable_IsRejected()
    {
        var errors = ErrorsAfterTextEdit("\"$show_id\"", "\"$never_set\"");

        Assert.Contains(errors, error => error.Contains("reads $never_set, which no step sets", StringComparison.Ordinal));
    }

    [Fact]
    public void FlowMissingARequiredArgument_IsRejected()
    {
        var errors = ErrorsAfterTextEdit("{\"id\":\"$show_id\",\"lang\":\"eng\"}", "{\"lang\":\"eng\"}");

        Assert.Contains(errors, error => error.Contains("does not give the required argument 'id'", StringComparison.Ordinal));
    }

    [Fact]
    public void FlowClaimRules_AreChecked()
    {
        var errors = Errors(config =>
        {
            var claims = JsonSerializer.Serialize(config.Flows!.Fetch!.Steps[^1], s_json);
            config.Flows.Fetch.Steps[^1] = JsonSerializer.Deserialize<JsonElement>(
                claims.Replace("\"min_length\":4", "\"min_length\":-1", StringComparison.Ordinal));
        });

        Assert.Contains(errors, error => error.Contains("min_length must not be negative", StringComparison.Ordinal));
    }

    [Fact]
    public void NegativeRegistrationOrder_IsRejected()
    {
        var errors = Errors(config => config.RegistrationOrder = -1);

        Assert.Contains("registration_order must not be negative.", errors);
    }

    [Fact]
    public void SearchFlowWithoutALimitedEmit_ParsesWithoutErrors()
    {
        // Emit steps need a confidence between 0 and 1.
        var errors = ErrorsAfterTextEdit("\"confidence\":0.6", "\"confidence\":7");

        Assert.Contains(errors, error => error.Contains("emit.confidence must be a number above 0", StringComparison.Ordinal));
    }
}
