using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using MediaEngine.Api.Services.Plugins.ApplicationServices;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using ApplicationEntity = MediaEngine.Domain.Entities.Application;

namespace MediaEngine.Api.Services;

public sealed class ApplicationAdministrationService(
    IApplicationRepository applications,
    IPermissionRegistry permissionRegistry,
    IAuthorizationEvaluator authorizationEvaluator,
    IAccountAccessDecisionService accountAccess,
    IAuthorizationInvalidationService invalidation,
    IAuthorizationAuditWriter auditWriter,
    TimeProvider timeProvider,
    IPluginApplicationPermissionAvailability? pluginPermissionAvailability = null)
{
    private const int CredentialByteLength = 48;

    public async Task<IReadOnlyList<ApplicationPermissionDefinitionDto>> GetPermissionDefinitionsAsync(
        RequestAuthority authority,
        CancellationToken ct = default)
    {
        await RequireAuthorityAsync(authority, ct).ConfigureAwait(false);
        return Array.AsReadOnly(permissionRegistry.GetAll().Select(MapCurrentDefinition).ToArray());
    }

    public async Task<IReadOnlyList<ApplicationPermissionPresetDto>> GetPermissionPresetsAsync(
        RequestAuthority authority,
        CancellationToken ct = default)
    {
        await RequireAuthorityAsync(authority, ct).ConfigureAwait(false);
        var values = ApplicationPermissionPresets.All.Select(preset =>
        {
            var permissions = preset.IsAdministrator
                ? permissionRegistry.GetAll()
                    .Where(IsCurrentlyAvailable)
                    .Select(definition => definition.Id.Value)
                : preset.Permissions
                    .Where(id => permissionRegistry.TryGet(id, out var definition) && IsCurrentlyAvailable(definition))
                    .Select(id => id.Value);
            return new ApplicationPermissionPresetDto(
                preset.Id,
                preset.DisplayName,
                Array.AsReadOnly(permissions.Order(StringComparer.Ordinal).ToArray()),
                preset.IsAdministrator);
        }).ToArray();
        return Array.AsReadOnly(values);
    }

    public async Task<IReadOnlyList<ApplicationResponse>> GetApplicationsAsync(
        RequestAuthority authority,
        CancellationToken ct = default)
    {
        await RequireAuthorityAsync(authority, ct).ConfigureAwait(false);
        var entities = await applications.GetApplicationsAsync(ct).ConfigureAwait(false);
        var responses = new ApplicationResponse[entities.Count];
        for (var index = 0; index < entities.Count; index++)
        {
            responses[index] = await MapApplicationAsync(entities[index], ct).ConfigureAwait(false);
        }

        return Array.AsReadOnly(responses);
    }

    public async Task<ApplicationResponse> GetApplicationAsync(
        RequestAuthority authority,
        Guid applicationId,
        CancellationToken ct = default)
    {
        await RequireAuthorityAsync(authority, ct).ConfigureAwait(false);
        var application = await RequireApplicationAsync(applicationId, ct).ConfigureAwait(false);
        return await MapApplicationAsync(application, ct).ConfigureAwait(false);
    }

    public async Task<ApplicationResponse> CreateApplicationAsync(
        RequestAuthority authority,
        CreateApplicationRequest request,
        CancellationToken ct = default)
    {
        await RequireAuthorityAsync(authority, ct).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        var type = MapApplicationType(request.ApplicationType);
        var requestedPermissions = ValidatePermissions(request.PermissionIds, type);
        var permissions = request.IsAdministrator
            ? permissionRegistry.GetAvailableFor(type).Select(definition => definition.Id).ToFrozenSet()
            : requestedPermissions;
        var application = new ApplicationEntity
        {
            Id = Guid.NewGuid(),
            Name = RequireName(request.Name, "Application"),
            Description = NormalizeDescription(request.Description),
            ApplicationType = type,
            IsEnabled = true,
            IsAdministrator = request.IsAdministrator,
            AuthorizationVersion = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await applications.InsertApplicationAsync(application, permissions, ct).ConfigureAwait(false);
        await InvalidateAndAuditAsync(authority, application.Id, "application.created", now, new Dictionary<string, string?>
        {
            ["name"] = application.Name,
            ["application_type"] = application.ApplicationType.ToString(),
            ["is_administrator"] = application.IsAdministrator.ToString(),
            ["permission_count"] = permissions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }, ct).ConfigureAwait(false);
        return await MapApplicationAsync(application, ct).ConfigureAwait(false);
    }

    public async Task<ApplicationResponse> UpdateApplicationAsync(
        RequestAuthority authority,
        Guid applicationId,
        UpdateApplicationRequest request,
        CancellationToken ct = default)
    {
        await RequireAuthorityAsync(authority, ct).ConfigureAwait(false);
        var application = await RequireApplicationAsync(applicationId, ct).ConfigureAwait(false);
        var type = MapApplicationType(request.ApplicationType);
        var clientIds = await applications.GetApplicationClientBindingsAsync(applicationId, ct).ConfigureAwait(false);
        if (type != ApplicationType.UserClient && clientIds.Count > 0)
        {
            throw ApplicationAdministrationException.Validation(
                "Remove registered client identifiers before changing this Application from UserClient.");
        }
        if (!request.IsAdministrator)
        {
            var permissions = await applications.GetApplicationPermissionsAsync(applicationId, ct).ConfigureAwait(false);
            ValidatePermissionSet(permissions, type);
        }

        var now = timeProvider.GetUtcNow();
        var changes = new Dictionary<string, string?>
        {
            ["name_before"] = application.Name,
            ["name_after"] = RequireName(request.Name, "Application"),
            ["application_type_before"] = application.ApplicationType.ToString(),
            ["application_type_after"] = type.ToString(),
            ["is_enabled_before"] = application.IsEnabled.ToString(),
            ["is_enabled_after"] = request.IsEnabled.ToString(),
            ["is_administrator_before"] = application.IsAdministrator.ToString(),
            ["is_administrator_after"] = request.IsAdministrator.ToString(),
        };
        application.Name = changes["name_after"]!;
        application.Description = NormalizeDescription(request.Description);
        application.ApplicationType = type;
        application.IsEnabled = request.IsEnabled;
        application.IsAdministrator = request.IsAdministrator;
        application.UpdatedAt = now;

        try
        {
            await applications.UpdateApplicationAsync(application, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            throw ApplicationAdministrationException.NotFound("Application not found.");
        }
        await InvalidateAndAuditAsync(
            authority,
            applicationId,
            "application.updated",
            now,
            changes,
            ct).ConfigureAwait(false);
        return await MapApplicationAsync(
            await RequireApplicationAsync(applicationId, ct).ConfigureAwait(false),
            ct).ConfigureAwait(false);
    }

    public async Task<ApplicationResponse> ReplaceClientBindingsAsync(
        RequestAuthority authority,
        Guid applicationId,
        SetApplicationClientBindingsRequest request,
        CancellationToken ct = default)
    {
        await RequireAuthorityAsync(authority, ct).ConfigureAwait(false);
        var application = await RequireApplicationAsync(applicationId, ct).ConfigureAwait(false);
        if (application.ApplicationType != ApplicationType.UserClient)
        {
            throw ApplicationAdministrationException.Validation(
                "Client identifiers can be registered only to a UserClient Application.");
        }

        var clientIds = ValidateClientIds(request.ClientIds);
        var now = timeProvider.GetUtcNow();
        try
        {
            await applications.ReplaceClientBindingsAsync(applicationId, clientIds, now, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            throw ApplicationAdministrationException.Conflict(exception.Message);
        }

        await InvalidateAndAuditAsync(
            authority,
            applicationId,
            "application.client_bindings_changed",
            now,
            new Dictionary<string, string?>
            {
                ["client_ids"] = string.Join(",", clientIds.Order(StringComparer.Ordinal)),
            },
            ct).ConfigureAwait(false);
        return await MapApplicationAsync(
            await RequireApplicationAsync(applicationId, ct).ConfigureAwait(false),
            ct).ConfigureAwait(false);
    }

    public async Task DeleteApplicationAsync(
        RequestAuthority authority,
        Guid applicationId,
        CancellationToken ct = default)
    {
        await RequireAuthorityAsync(authority, ct).ConfigureAwait(false);
        var application = await RequireApplicationAsync(applicationId, ct).ConfigureAwait(false);
        if (applicationId == BuiltInApplicationIds.NativeClient)
        {
            throw ApplicationAdministrationException.Conflict("Your Library apps use this integration, so it can be switched off or have its keys revoked, but not deleted.");
        }

        var now = timeProvider.GetUtcNow();
        if (!await applications.DeleteApplicationAsync(applicationId, now, ct).ConfigureAwait(false))
        {
            throw ApplicationAdministrationException.NotFound("Application not found.");
        }

        await InvalidateAndAuditAsync(authority, applicationId, "application.deleted", now, new Dictionary<string, string?>
        {
            ["name"] = application.Name,
            ["application_type"] = application.ApplicationType.ToString(),
        }, ct).ConfigureAwait(false);
    }

    public async Task<ApplicationResponse> ReplacePermissionsAsync(
        RequestAuthority authority,
        Guid applicationId,
        SetApplicationPermissionsRequest request,
        CancellationToken ct = default)
    {
        await RequireAuthorityAsync(authority, ct).ConfigureAwait(false);
        var application = await RequireApplicationAsync(applicationId, ct).ConfigureAwait(false);
        var permissions = ValidatePermissions(request.PermissionIds, application.ApplicationType);
        var now = timeProvider.GetUtcNow();
        try
        {
            await applications.ReplacePermissionsAsync(applicationId, permissions, now, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            throw ApplicationAdministrationException.NotFound("Application not found.");
        }
        await InvalidateAndAuditAsync(authority, applicationId, "application.permissions_changed", now, new Dictionary<string, string?>
        {
            ["permission_ids"] = string.Join(",", permissions.Select(value => value.Value).Order(StringComparer.Ordinal)),
        }, ct).ConfigureAwait(false);
        return await MapApplicationAsync(
            await RequireApplicationAsync(applicationId, ct).ConfigureAwait(false),
            ct).ConfigureAwait(false);
    }

    public async Task<ApplicationCredentialIssuedResponse> IssueCredentialAsync(
        RequestAuthority authority,
        Guid applicationId,
        CreateApplicationCredentialRequest request,
        CancellationToken ct = default)
    {
        await RequireAuthorityAsync(authority, ct).ConfigureAwait(false);
        var application = await RequireApplicationAsync(applicationId, ct).ConfigureAwait(false);
        EnsureEnabled(application);
        var now = timeProvider.GetUtcNow();
        ValidateExpiry(request.ExpiresAt, now);
        var issued = CreateCredential(applicationId, RequireName(request.Name, "Credential"), request.ExpiresAt, now);
        try
        {
            await applications.InsertCredentialAsync(issued.Credential, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            throw ApplicationAdministrationException.Conflict(
                "The application was disabled or removed before the credential could be issued.");
        }
        await InvalidateAndAuditAsync(authority, applicationId, "application.credential_issued", now, new Dictionary<string, string?>
        {
            ["credential_id"] = issued.Credential.Id.ToString("D"),
            ["credential_name"] = issued.Credential.Name,
            ["expires_at"] = issued.Credential.ExpiresAt?.ToString("O"),
        }, ct).ConfigureAwait(false);
        return new(MapCredential(issued.Credential), issued.Plaintext);
    }

    public async Task<ApplicationCredentialIssuedResponse> RotateCredentialAsync(
        RequestAuthority authority,
        Guid applicationId,
        Guid credentialId,
        RotateApplicationCredentialRequest request,
        CancellationToken ct = default)
    {
        await RequireAuthorityAsync(authority, ct).ConfigureAwait(false);
        var application = await RequireApplicationAsync(applicationId, ct).ConfigureAwait(false);
        EnsureEnabled(application);
        var credentials = await applications.GetApplicationCredentialsAsync(applicationId, ct).ConfigureAwait(false);
        var prior = credentials.SingleOrDefault(value => value.Id == credentialId)
            ?? throw ApplicationAdministrationException.NotFound("Application credential not found.");
        var now = timeProvider.GetUtcNow();
        ValidateExpiry(request.ExpiresAt, now);
        var name = request.Name is null ? prior.Name : RequireName(request.Name, "Credential");
        var issued = CreateCredential(applicationId, name, request.ExpiresAt, now);
        if (!await applications.RotateCredentialAsync(applicationId, credentialId, issued.Credential, now, ct).ConfigureAwait(false))
        {
            throw ApplicationAdministrationException.Conflict("The credential was already revoked or rotated.");
        }

        await InvalidateAndAuditAsync(authority, applicationId, "application.credential_rotated", now, new Dictionary<string, string?>
        {
            ["prior_credential_id"] = credentialId.ToString("D"),
            ["replacement_credential_id"] = issued.Credential.Id.ToString("D"),
            ["credential_name"] = issued.Credential.Name,
            ["expires_at"] = issued.Credential.ExpiresAt?.ToString("O"),
        }, ct).ConfigureAwait(false);
        return new(MapCredential(issued.Credential), issued.Plaintext);
    }

    public async Task RevokeCredentialAsync(
        RequestAuthority authority,
        Guid applicationId,
        Guid credentialId,
        CancellationToken ct = default)
    {
        await RequireAuthorityAsync(authority, ct).ConfigureAwait(false);
        await RequireApplicationAsync(applicationId, ct).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        if (!await applications.RevokeCredentialAsync(applicationId, credentialId, now, ct).ConfigureAwait(false))
        {
            throw ApplicationAdministrationException.NotFound("Active application credential not found.");
        }

        await InvalidateAndAuditAsync(authority, applicationId, "application.credential_revoked", now, new Dictionary<string, string?>
        {
            ["credential_id"] = credentialId.ToString("D"),
        }, ct).ConfigureAwait(false);
    }

    private async ValueTask RequireAuthorityAsync(RequestAuthority authority, CancellationToken ct)
    {
        AuthorizationDecision decision;
        if (authority.PrincipalKind == PrincipalKind.Human)
        {
            decision = await accountAccess.EvaluateAdministratorAsync(authority, true, ct).ConfigureAwait(false);
        }
        else if (authority.PrincipalKind == PrincipalKind.DelegatedUserClient)
        {
            decision = await authorizationEvaluator.EvaluateAsync(
                authority,
                new AuthorizationRequirement(ApplicationPermission: ApplicationPermissionIds.IdentityApplicationsWrite),
                null,
                ct).ConfigureAwait(false);
            if (decision.IsAllowed)
            {
                decision = await accountAccess.EvaluateAdministratorAsync(authority, true, ct).ConfigureAwait(false);
            }
        }
        else if (authority.PrincipalKind == PrincipalKind.ServiceApplication)
        {
            decision = await authorizationEvaluator.EvaluateAsync(
                authority,
                new AuthorizationRequirement(ApplicationPermission: ApplicationPermissionIds.IdentityApplicationsWrite),
                null,
                ct).ConfigureAwait(false);
        }
        else
        {
            decision = AuthorizationDecision.Deny(
                authority.IsAuthenticated
                    ? AuthorizationDenialReason.WrongPrincipalKind
                    : AuthorizationDenialReason.Unauthenticated);
        }

        if (decision.IsAllowed)
        {
            return;
        }

        throw decision.DenialReason == AuthorizationDenialReason.Unauthenticated
            ? ApplicationAdministrationException.Unauthorized("Authentication is required.")
            : ApplicationAdministrationException.Forbidden("Application administration permission is required.");
    }

    private async Task<ApplicationEntity> RequireApplicationAsync(Guid applicationId, CancellationToken ct)
    {
        if (applicationId == Guid.Empty)
        {
            throw ApplicationAdministrationException.NotFound("Application not found.");
        }

        return await applications.GetApplicationAsync(applicationId, ct).ConfigureAwait(false)
            ?? throw ApplicationAdministrationException.NotFound("Application not found.");
    }

    private async Task<ApplicationResponse> MapApplicationAsync(ApplicationEntity application, CancellationToken ct)
    {
        var permissions = application.IsAdministrator
            ? permissionRegistry.GetAvailableFor(application.ApplicationType)
                .Select(definition => definition.Id.Value)
                .Order(StringComparer.Ordinal)
                .ToArray()
            : (await applications.GetApplicationPermissionsAsync(application.Id, ct).ConfigureAwait(false))
                .Select(value => value.Value)
                .Order(StringComparer.Ordinal)
                .ToArray();
        var credentials = (await applications.GetApplicationCredentialsAsync(application.Id, ct).ConfigureAwait(false))
            .Select(MapCredential)
            .ToArray();
        var clientIds = (await applications.GetApplicationClientBindingsAsync(application.Id, ct).ConfigureAwait(false))
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new(
            application.Id,
            application.Name,
            application.Description,
            MapApplicationType(application.ApplicationType),
            application.IsEnabled,
            application.IsAdministrator,
            application.AuthorizationVersion,
            Array.AsReadOnly(permissions),
            Array.AsReadOnly(clientIds),
            Array.AsReadOnly(credentials),
            application.CreatedAt,
            application.UpdatedAt,
            application.LastUsedAt);
    }

    private IReadOnlySet<ApplicationPermissionId> ValidatePermissions(
        IReadOnlyList<string> permissionIds,
        ApplicationType applicationType)
    {
        if (permissionIds is null)
        {
            throw ApplicationAdministrationException.Validation("Permission identifiers are required.");
        }

        var values = new HashSet<ApplicationPermissionId>();
        foreach (var rawId in permissionIds)
        {
            ApplicationPermissionId id;
            try
            {
                id = new ApplicationPermissionId(rawId);
            }
            catch (ArgumentException)
            {
                throw ApplicationAdministrationException.Validation($"Permission identifier '{rawId}' is invalid.");
            }

            if (!permissionRegistry.TryGet(id, out var definition))
            {
                throw ApplicationAdministrationException.Validation($"Permission '{id}' is not registered.");
            }

            if (!definition.IsAvailable)
            {
                throw ApplicationAdministrationException.Validation(
                    $"Permission '{id}' is unavailable: {definition.UnavailableReason}");
            }

            if (!definition.ApplicationTypes.Contains(applicationType))
            {
                throw ApplicationAdministrationException.Validation(
                    $"Permission '{id}' is not available to {applicationType} applications.");
            }

            values.Add(id);
        }
        return values.ToFrozenSet();
    }

    private void ValidatePermissionSet(
        IReadOnlySet<ApplicationPermissionId> permissionIds,
        ApplicationType applicationType)
    {
        foreach (var id in permissionIds)
        {
            if (!permissionRegistry.TryGet(id, out var definition) ||
                !definition.IsAvailable ||
                !definition.ApplicationTypes.Contains(applicationType))
            {
                throw ApplicationAdministrationException.Validation(
                    $"Existing permission '{id}' is unavailable for {applicationType} applications.");
            }
        }
    }

    private static IReadOnlySet<string> ValidateClientIds(IReadOnlyList<string> clientIds)
    {
        if (clientIds is null)
        {
            throw ApplicationAdministrationException.Validation("Client identifiers are required.");
        }

        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in clientIds)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw ApplicationAdministrationException.Validation("Client identifiers cannot be empty.");
            }

            var normalized = value.Trim();
            if (normalized.Length > 200 || normalized.Any(char.IsControl) || normalized.Any(char.IsWhiteSpace))
            {
                throw ApplicationAdministrationException.Validation(
                    "Client identifiers must be at most 200 characters and cannot contain whitespace.");
            }
            values.Add(normalized);
        }
        return values.ToFrozenSet(StringComparer.Ordinal);
    }

    private async Task InvalidateAndAuditAsync(
        RequestAuthority authority,
        Guid applicationId,
        string eventType,
        DateTimeOffset occurredAt,
        IReadOnlyDictionary<string, string?> changes,
        CancellationToken ct)
    {
        await invalidation.InvalidateApplicationAsync(applicationId, ct).ConfigureAwait(false);
        await auditWriter.WriteAsync(new AuthorizationAuditEvent(
            eventType,
            occurredAt,
            authority.AccountId,
            authority.ActiveProfileId,
            authority.ApplicationId,
            "application",
            applicationId.ToString("D"),
            changes), ct).ConfigureAwait(false);
    }

    private static (ApplicationCredential Credential, string Plaintext) CreateCredential(
        Guid applicationId,
        string name,
        DateTimeOffset? expiresAt,
        DateTimeOffset createdAt)
    {
        var plaintext = "tuvima_app_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(CredentialByteLength))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext)));
        return (new ApplicationCredential
        {
            Id = Guid.NewGuid(),
            ApplicationId = applicationId,
            Name = name,
            CredentialHash = hash,
            HashScheme = "sha256",
            CreatedAt = createdAt,
            ExpiresAt = expiresAt,
        }, plaintext);
    }

    private static ApplicationPermissionDefinitionDto MapDefinition(PermissionDefinition definition) => new(
        definition.Id.Value,
        definition.Category,
        definition.DisplayName,
        definition.Description,
        definition.RiskLevel.ToString().ToLowerInvariant(),
        Array.AsReadOnly(definition.ApplicationTypes
            .OrderBy(type => type)
            .Select(MapApplicationType)
            .ToArray()),
        definition.RequiresUserContext,
        definition.Provenance == PermissionProvenance.BuiltIn ? "built-in" : "plugin",
        definition.PluginId,
        definition.SortOrder,
        definition.IsAvailable,
        definition.UnavailableReason);

    private ApplicationPermissionDefinitionDto MapCurrentDefinition(PermissionDefinition definition)
    {
        var mapped = MapDefinition(definition);
        if (pluginPermissionAvailability?.TryGetAvailability(
                definition.Id.Value,
                out var available,
                out var reason) != true)
        {
            return mapped;
        }

        return mapped with
        {
            IsAvailable = available,
            UnavailableReason = available ? null : reason ?? "The plugin service is unavailable.",
        };
    }

    private bool IsCurrentlyAvailable(PermissionDefinition definition) =>
        definition.IsAvailable
        && (pluginPermissionAvailability?.TryGetAvailability(
                definition.Id.Value,
                out var available,
                out _) != true
            || available);

    private static ApplicationCredentialResponse MapCredential(ApplicationCredential credential) => new(
        credential.Id,
        credential.ApplicationId,
        credential.Name,
        credential.CreatedAt,
        credential.ExpiresAt,
        credential.LastUsedAt,
        credential.RevokedAt);

    private static string RequireName(string value, string subject)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw ApplicationAdministrationException.Validation($"{subject} name is required.");
        }

        var normalized = value.Trim();
        if (normalized.Length > 120)
        {
            throw ApplicationAdministrationException.Validation($"{subject} name cannot exceed 120 characters.");
        }

        return normalized;
    }

    private static string? NormalizeDescription(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.Length > 1000)
        {
            throw ApplicationAdministrationException.Validation("Application description cannot exceed 1000 characters.");
        }

        return normalized;
    }

    private static void ValidateExpiry(DateTimeOffset? expiresAt, DateTimeOffset now)
    {
        if (expiresAt is not null && expiresAt <= now)
        {
            throw ApplicationAdministrationException.Validation("Credential expiry must be in the future.");
        }
    }

    private static void EnsureEnabled(ApplicationEntity application)
    {
        if (!application.IsEnabled)
        {
            throw ApplicationAdministrationException.Conflict("Enable the application before issuing a credential.");
        }
    }

    private static ApplicationType MapApplicationType(ApplicationTypeDto value) => value switch
    {
        ApplicationTypeDto.UserClient => ApplicationType.UserClient,
        ApplicationTypeDto.ServerIntegration => ApplicationType.ServerIntegration,
        ApplicationTypeDto.Automation => ApplicationType.Automation,
        ApplicationTypeDto.Other => ApplicationType.Other,
        _ => throw ApplicationAdministrationException.Validation("Application type is invalid."),
    };

    private static ApplicationTypeDto MapApplicationType(ApplicationType value) => value switch
    {
        ApplicationType.UserClient => ApplicationTypeDto.UserClient,
        ApplicationType.ServerIntegration => ApplicationTypeDto.ServerIntegration,
        ApplicationType.Automation => ApplicationTypeDto.Automation,
        ApplicationType.Other => ApplicationTypeDto.Other,
        _ => throw new InvalidOperationException($"Unsupported application type '{value}'."),
    };
}

public enum ApplicationAdministrationError
{
    Unauthorized,
    Forbidden,
    NotFound,
    Conflict,
    Validation,
}

public sealed class ApplicationAdministrationException : Exception
{
    private ApplicationAdministrationException(ApplicationAdministrationError error, string message)
        : base(message)
    {
        Error = error;
    }

    public ApplicationAdministrationError Error { get; }

    public static ApplicationAdministrationException Unauthorized(string message) =>
        new(ApplicationAdministrationError.Unauthorized, message);

    public static ApplicationAdministrationException Forbidden(string message) =>
        new(ApplicationAdministrationError.Forbidden, message);

    public static ApplicationAdministrationException NotFound(string message) =>
        new(ApplicationAdministrationError.NotFound, message);

    public static ApplicationAdministrationException Conflict(string message) =>
        new(ApplicationAdministrationError.Conflict, message);

    public static ApplicationAdministrationException Validation(string message) =>
        new(ApplicationAdministrationError.Validation, message);
}
