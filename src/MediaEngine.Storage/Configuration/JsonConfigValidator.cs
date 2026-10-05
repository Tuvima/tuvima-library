using System.Text.RegularExpressions;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;

namespace MediaEngine.Storage.Configuration;

public static class JsonConfigValidator
{
    private static readonly Regex HexColorRegex = new("^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$", RegexOptions.Compiled);
    private static readonly Regex AuthProviderIdRegex = new("^[a-z][a-z0-9-]{1,39}$", RegexOptions.Compiled);

    public static IReadOnlyList<string> Validate<T>(T config, string relativePath)
        where T : class
    {
        var errors = new List<string>();

        switch (config)
        {
            case CoreConfiguration core:
                ValidateCore(core, errors);
                break;
            case ProviderConfiguration provider:
                ValidateProvider(provider, relativePath, errors);
                break;
            case HydrationSettings hydration:
                AddPositive(errors, hydration.Stage1TimeoutSeconds, "stage1_timeout_seconds");
                AddPositive(errors, hydration.QuickHydrationTimeoutSeconds, "quick_hydration_timeout_seconds");
                AddRange(errors, hydration.AutoReviewConfidenceThreshold, "auto_review_confidence_threshold", 0, 1);
                AddRange(errors, hydration.RetailAutoAcceptThreshold, "retail_auto_accept_threshold", 0, 1);
                AddRange(errors, hydration.RetailAmbiguousThreshold, "retail_ambiguous_threshold", 0, 1);
                break;
            case ScoringSettings scoring:
                AddRange(errors, scoring.AutoLinkThreshold, "auto_link_threshold", 0, 1);
                AddRange(errors, scoring.ConflictThreshold, "conflict_threshold", 0, 1);
                AddRange(errors, scoring.ConflictEpsilon, "conflict_epsilon", 0, 1);
                AddPositive(errors, scoring.StaleClaimDecayDays, "stale_claim_decay_days");
                AddRange(errors, scoring.StaleClaimDecayFactor, "stale_claim_decay_factor", 0, 1);
                break;
            case MaintenanceSettings maintenance:
                AddPositive(errors, maintenance.ActivityRetentionDays, "activity_retention_days");
                AddPositive(errors, maintenance.MaxTransactionLogEntries, "max_transaction_log_entries");
                AddPositive(errors, maintenance.WeeklySyncIntervalDays, "weekly_sync_interval_days");
                AddPositive(errors, maintenance.WeeklySyncBatchSize, "weekly_sync_batch_size");
                break;
            case MediaTypeConfiguration mediaTypes:
                ValidateMediaTypes(mediaTypes, errors);
                break;
            case Dictionary<string, MediaTypePipeline> pipelines:
                ValidatePipelines(pipelines, errors);
                break;
            case PaletteConfiguration palette:
                ValidatePalette(palette, errors);
                break;
            case LibraryPreferencesSettings preferences:
                ValidateLibraryPreferences(preferences, errors);
                break;
            case LibrariesConfiguration libraries:
                ValidateLibraries(libraries, errors);
                break;
            case NetworkSettings network:
                ValidateNetwork(network, errors);
                break;
        }

        return errors;
    }

    private static void ValidateNetwork(NetworkSettings settings, List<string> errors)
    {
        if (!string.Equals(settings.SchemaVersion, "3.0", StringComparison.Ordinal))
        {
            errors.Add("schema_version must be 3.0; pre-beta network configuration is not migrated in place.");
        }

        if (settings.Local.Port is < 1 or > 65535)
        {
            errors.Add("local.port must be between 1 and 65535.");
        }

        if (!Allowed(settings.Local.BindMode, NetworkBindModes.Automatic, NetworkBindModes.SpecificInterface))
        {
            errors.Add("local.bind_mode must be automatic or specific-interface.");
        }

        if (settings.Local.BindMode == NetworkBindModes.SpecificInterface
            && string.IsNullOrWhiteSpace(settings.Local.InterfaceId))
        {
            errors.Add("local.interface_id is required when bind_mode is specific-interface.");
        }

        var serverName = settings.Local.PreferredServerName?.Trim() ?? string.Empty;
        if (serverName.Length is < 1 or > 63
            || serverName.StartsWith('-')
            || serverName.EndsWith('-')
            || serverName.Any(character => !char.IsLetterOrDigit(character) && character != '-'))
        {
            errors.Add("local.preferred_server_name must be a 1-63 character DNS label containing only letters, numbers, and hyphens.");
        }

        if (!Allowed(settings.Remote.ConnectionMode,
                NetworkConnectionModes.LocalOnly,
                NetworkConnectionModes.Tailscale,
                NetworkConnectionModes.DirectOnly,
                NetworkConnectionModes.Custom))
        {
            errors.Add("remote.connection_mode is unsupported.");
        }

        if (settings.Remote.Enabled && settings.Remote.ConnectionMode == NetworkConnectionModes.LocalOnly)
        {
            errors.Add("remote.connection_mode must select tailscale, custom, or direct-only when remote access is enabled.");
        }

        if (settings.Remote.ExternalPort is < 1 or > 65535)
        {
            errors.Add("remote.external_port must be between 1 and 65535 when provided.");
        }

        if (settings.Remote.TlsTerminationPort is < 1 or > 65535)
        {
            errors.Add("remote.tls_termination_port must be between 1 and 65535 when provided.");
        }

        if (settings.Remote.ConnectionMode is NetworkConnectionModes.Custom or NetworkConnectionModes.DirectOnly
            && (!Uri.TryCreate(settings.Remote.PublicHostname, UriKind.Absolute, out var publicUri)
                || publicUri.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add("remote.public_hostname must be an absolute HTTPS URL for custom and direct-only modes.");
        }

        if (settings.Remote.AutomaticRouterConfiguration
            && settings.Remote.ConnectionMode != NetworkConnectionModes.DirectOnly)
        {
            errors.Add("remote.automatic_router_configuration is available only in direct-only mode.");
        }

        if (settings.Remote.AutomaticRouterConfiguration && settings.Remote.TlsTerminationPort is null)
        {
            errors.Add("remote.tls_termination_port is required for automatic router configuration so the Dashboard is never mapped directly.");
        }

        foreach (var proxy in settings.Remote.TrustedProxies)
        {
            if (!System.Net.IPAddress.TryParse(proxy, out _))
            {
                errors.Add($"remote.trusted_proxies contains an invalid IP address: '{proxy}'.");
            }
        }

        foreach (var network in settings.Remote.TrustedProxyNetworks)
        {
            if (!System.Net.IPNetwork.TryParse(network, out _))
            {
                errors.Add($"remote.trusted_proxy_networks contains an invalid CIDR network: '{network}'.");
            }
        }

        if (!Allowed(settings.Streaming.RemoteQuality,
                RemoteStreamingQualities.Automatic,
                RemoteStreamingQualities.Original,
                RemoteStreamingQualities.Hd1080,
                RemoteStreamingQualities.Hd720,
                RemoteStreamingQualities.DataSaver))
        {
            errors.Add("streaming.remote_quality is unsupported.");
        }

        if (settings.Streaming.ReservedUploadMbps is < 0 or > 10_000)
        {
            errors.Add("streaming.reserved_upload_mbps must be between 0 and 10000.");
        }

        if (!Allowed(settings.Streaming.ConcurrentRemoteStreams, RemoteStreamConcurrencyModes.Automatic))
        {
            errors.Add("streaming.concurrent_remote_streams must be automatic.");
        }
    }

    private static void ValidateCore(CoreConfiguration core, List<string> errors)
    {
        AddRequired(errors, core.SchemaVersion, "schema_version");
        AddRequired(errors, core.DatabasePath, "database_path");
        AddRequired(errors, core.ServerName, "server_name");
        foreach (var property in core.UnmappedProperties?.Keys ?? Enumerable.Empty<string>())
        {
            errors.Add($"$.{property} is not supported by core schema 2.0.");
        }
        if (!string.IsNullOrWhiteSpace(core.Country) && core.Country.Length != 2)
        {
            errors.Add("country must be a two-letter country code.");
        }

        if (!Allowed(core.DateFormat, "system", "short", "medium", "long", "iso8601"))
        {
            errors.Add("date_format must be one of system, short, medium, long, iso8601.");
        }

        if (!Allowed(core.TimeFormat, "system", "12h", "24h"))
        {
            errors.Add("time_format must be one of system, 12h, 24h.");
        }

        ValidateAuthentication(core.Auth, errors);

        AddPositive(errors, core.Pipeline.LeaseSizes.Retail, "pipeline.lease_sizes.retail");
        AddPositive(errors, core.Pipeline.LeaseSizes.Wikidata, "pipeline.lease_sizes.wikidata");
        AddPositive(errors, core.Pipeline.LeaseSizes.Hydration, "pipeline.lease_sizes.hydration");
        AddPositive(errors, core.Pipeline.BatchGate.TimeoutSeconds, "pipeline.batch_gate.timeout_seconds");
    }

    private static void ValidateAuthentication(AuthSettings auth, List<string> errors)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < auth.ExternalProviders.Count; index++)
        {
            var provider = auth.ExternalProviders[index];
            var prefix = $"auth.external_providers[{index}]";
            if (!AuthProviderIdRegex.IsMatch(provider.Id))
            {
                errors.Add($"{prefix}.id must contain 2 to 40 lowercase letters, numbers, or hyphens and begin with a letter.");
            }
            else if (!ids.Add(provider.Id))
            {
                errors.Add($"{prefix}.id must be unique.");
            }

            if (!Allowed(provider.Kind, ExternalAuthProviderKinds.OpenIdConnect, ExternalAuthProviderKinds.OAuth))
            {
                errors.Add($"{prefix}.kind must be oidc or oauth.");
            }

            AddRequired(errors, provider.DisplayName, $"{prefix}.display_name");
            if (!provider.Enabled)
            {
                continue;
            }

            AddRequired(errors, provider.ClientId, $"{prefix}.client_id");
            if (provider.Kind.Equals(ExternalAuthProviderKinds.OpenIdConnect, StringComparison.OrdinalIgnoreCase))
            {
                AddHttpsUri(errors, provider.Authority, $"{prefix}.authority");
                if (!provider.Scopes.Contains("openid", StringComparer.Ordinal))
                {
                    errors.Add($"{prefix}.scopes must contain openid for an OIDC provider.");
                }
            }
            else
            {
                AddHttpsUri(errors, provider.Issuer, $"{prefix}.issuer");
                AddHttpsUri(errors, provider.AuthorizationEndpoint, $"{prefix}.authorization_endpoint");
                AddHttpsUri(errors, provider.TokenEndpoint, $"{prefix}.token_endpoint");
                AddHttpsUri(errors, provider.UserInformationEndpoint, $"{prefix}.user_information_endpoint");
                AddRequired(errors, provider.IdClaim, $"{prefix}.id_claim");
            }
        }
    }

    private static void AddHttpsUri(List<string> errors, string value, string field)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            errors.Add($"{field} must be an absolute HTTPS URL.");
        }
    }

    private static void ValidateLibraries(LibrariesConfiguration config, List<string> errors)
    {
        if (!string.Equals(config.SchemaVersion, "6.0", StringComparison.Ordinal))
        {
            errors.Add("schema_version must be 6.0; pre-beta library configuration is not migrated in place.");
        }

        AddNoUnmappedProperties(config.UnmappedProperties, "$", errors);

        var storageLocationIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var storageLocationPaths = new List<(string Path, string Field)>();
        for (var index = 0; index < config.StorageLocations.Count; index++)
        {
            var location = config.StorageLocations[index];
            var prefix = $"storage_locations[{index}]";
            AddRequired(errors, location.Id, $"{prefix}.id");
            AddRequired(errors, location.Label, $"{prefix}.label");
            AddRequired(errors, location.Path, $"{prefix}.path");
            if (!string.IsNullOrWhiteSpace(location.Id) && !storageLocationIds.Add(location.Id))
            {
                errors.Add($"{prefix}.id must be unique.");
            }

            if (TryNormalizePath(location.Path, out var normalizedPath))
            {
                storageLocationPaths.Add((normalizedPath, $"{prefix}.path"));
            }
            else if (!string.IsNullOrWhiteSpace(location.Path))
            {
                errors.Add($"{prefix}.path must be an absolute path.");
            }

            AddNoUnmappedProperties(location.UnmappedProperties, prefix, errors);
        }

        if (config.StorageLocations.Count == 0)
        {
            errors.Add("storage_locations must contain at least one explicitly allowed server folder root.");
        }

        if (config.ViewStorage is null)
        {
            errors.Add("view_storage is required.");
        }
        else
        {
            AddRequired(errors, config.ViewStorage.StorageLocationId, "view_storage.storage_location_id");
            AddRequired(errors, config.ViewStorage.RelativeRoot, "view_storage.relative_root");
            AddNoUnmappedProperties(config.ViewStorage.UnmappedProperties, "view_storage", errors);
            var viewLocation = config.StorageLocations.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, config.ViewStorage.StorageLocationId, StringComparison.OrdinalIgnoreCase));
            if (viewLocation is null)
            {
                errors.Add("view_storage.storage_location_id must reference a configured storage location.");
            }
            else if (!viewLocation.AllowWrite)
            {
                errors.Add("view_storage.storage_location_id must reference a writable storage location.");
            }

            if (!string.IsNullOrWhiteSpace(config.ViewStorage.RelativeRoot)
                && (Path.IsPathRooted(config.ViewStorage.RelativeRoot)
                    || config.ViewStorage.RelativeRoot.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .Any(segment => segment == "..")))
            {
                errors.Add("view_storage.relative_root must be a contained relative path.");
            }
        }

        for (var left = 0; left < storageLocationPaths.Count; left++)
        {
            for (var right = left + 1; right < storageLocationPaths.Count; right++)
            {
                if (PathsOverlap(storageLocationPaths[left].Path, storageLocationPaths[right].Path))
                {
                    errors.Add($"{storageLocationPaths[left].Field} and {storageLocationPaths[right].Field} must not overlap.");
                }
            }
        }

        if (config.PersonalLibraryPolicy is null)
        {
            errors.Add("personal_library_policy is required.");
        }
        else
        {
            AddNoUnmappedProperties(config.PersonalLibraryPolicy.UnmappedProperties, "personal_library_policy", errors);
            if (!LibraryVisibility.IsValid(config.PersonalLibraryPolicy.DefaultVisibility))
            {
                errors.Add("personal_library_policy.default_visibility must be private, shared, or household.");
            }
        }

        var ids = new HashSet<Guid>();
        var sourceIds = new HashSet<Guid>();
        var normalizedPaths = new List<(string Path, string Field)>();
        for (var index = 0; index < config.Libraries.Count; index++)
        {
            var library = config.Libraries[index];
            var prefix = $"libraries[{index}]";
            if (!Guid.TryParse(library.Id, out var id) || id == Guid.Empty)
            {
                errors.Add($"{prefix}.id must be a non-empty GUID.");
            }
            else if (!ids.Add(id))
            {
                errors.Add($"{prefix}.id must be unique.");
            }

            AddRequired(errors, library.Name, $"{prefix}.name");
            if (!LibraryKinds.IsValid(library.Kind))
            {
                errors.Add($"{prefix}.kind must be catalogued or personal.");
            }
            else if (library.Kind == LibraryKinds.Personal)
            {
                errors.Add($"{prefix}.personal libraries are obsolete; View Personal Spaces and their sources are profile-owned records.");
            }

            if (!LibraryAreas.IsValid(library.Area))
            {
                errors.Add($"{prefix}.area must be one of read, watch, listen, view.");
            }

            if (!LibraryPresentations.IsValid(library.Presentation))
            {
                errors.Add($"{prefix}.presentation is unsupported.");
            }

            if (!LibraryMetadataPolicies.IsValid(library.MetadataPolicy))
            {
                errors.Add($"{prefix}.metadata_policy must be one of enriched, local_preferred, local_only, manual.");
            }

            if (!LibraryVisibility.IsValid(library.Visibility))
            {
                errors.Add($"{prefix}.visibility must be private, shared, or household.");
            }

            if (!LibraryDuplicatePolicies.IsValid(library.DuplicatePolicy))
            {
                errors.Add($"{prefix}.duplicate_policy must be skip_exact, keep_both, or replace_existing.");
            }

            if (library.OrganizationPolicy is null)
            {
                errors.Add($"{prefix}.organization_policy is required.");
            }
            else
            {
                ValidateOrganizationPolicy(library.OrganizationPolicy, $"{prefix}.organization_policy", errors);
            }

            ValidateLibrarySemantics(library, prefix, errors);
            ValidateProfileIds(library, prefix, errors);
            var intakeModes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var intakeMode in library.AcceptedIntakeModes)
            {
                if (!LibraryIntakeModes.IsValid(intakeMode))
                {
                    errors.Add($"{prefix}.accepted_intake_modes contains unsupported value '{intakeMode}'.");
                }
                else if (!intakeModes.Add(intakeMode))
                {
                    errors.Add($"{prefix}.accepted_intake_modes must not contain duplicates.");
                }
            }

            AddNoUnmappedProperties(library.UnmappedProperties, prefix, errors);


            for (var sourceIndex = 0; sourceIndex < library.Sources.Count; sourceIndex++)
            {
                var source = library.Sources[sourceIndex];
                var sourcePrefix = $"{prefix}.sources[{sourceIndex}]";
                ValidateLibrarySource(source, sourcePrefix, sourceIds, normalizedPaths, errors);
            }

            ValidatePrimaryDestination(library, prefix, errors);
        }

        for (var left = 0; left < normalizedPaths.Count; left++)
        {
            for (var right = left + 1; right < normalizedPaths.Count; right++)
            {
                var first = normalizedPaths[left];
                var second = normalizedPaths[right];
                if (PathsOverlap(first.Path, second.Path))
                {
                    errors.Add($"{first.Field} and {second.Field} must not overlap.");
                }
            }
        }
    }

    private static void ValidateLibrarySemantics(LibraryFolderConfig library, string prefix, List<string> errors)
    {
        if (library.Kind == LibraryKinds.Catalogued)
        {
            AddRequired(errors, library.Category, $"{prefix}.category");
            if (library.Area == LibraryAreas.View)
            {
                errors.Add($"{prefix}.catalogued libraries cannot use the view area.");
            }

            if (library.Presentation != LibraryPresentations.Catalogue)
            {
                errors.Add($"{prefix}.catalogued libraries must use the catalogue presentation.");
            }

            if (library.MetadataPolicy != LibraryMetadataPolicies.Enriched)
            {
                errors.Add($"{prefix}.catalogued libraries must use enriched metadata.");
            }

            if (!string.IsNullOrWhiteSpace(library.OwnerProfileId))
            {
                errors.Add($"{prefix}.catalogued libraries cannot have an owner_profile_id.");
            }
        }
        else if (library.Kind == LibraryKinds.Personal)
        {
            if (library.Area != LibraryAreas.View)
            {
                errors.Add($"{prefix}.personal libraries must use the view area.");
            }

            if (library.Presentation == LibraryPresentations.Catalogue)
            {
                errors.Add($"{prefix}.personal libraries cannot use the catalogue presentation.");
            }

            if (!LibraryMetadataPolicies.BypassesExternalIdentity(library.MetadataPolicy))
            {
                errors.Add($"{prefix}.personal libraries must use local_only or manual metadata.");
            }

            if (string.IsNullOrWhiteSpace(library.OwnerProfileId))
            {
                errors.Add($"{prefix}.owner_profile_id is required for personal libraries.");
            }
        }

        if (library.Visibility == LibraryVisibility.Shared && library.AuthorizedProfileIds.Count == 0)
        {
            errors.Add($"{prefix}.authorized_profile_ids must contain at least one profile when visibility is shared.");
        }

        if (library.Visibility != LibraryVisibility.Shared && library.AuthorizedProfileIds.Count > 0)
        {
            errors.Add($"{prefix}.authorized_profile_ids is only valid when visibility is shared.");
        }
    }

    private static void ValidateProfileIds(LibraryFolderConfig library, string prefix, List<string> errors)
    {
        if (!string.IsNullOrWhiteSpace(library.OwnerProfileId)
            && (!Guid.TryParse(library.OwnerProfileId, out var ownerId) || ownerId == Guid.Empty))
        {
            errors.Add($"{prefix}.owner_profile_id must be a non-empty GUID.");
        }

        var authorizedIds = new HashSet<Guid>();
        foreach (var value in library.AuthorizedProfileIds)
        {
            if (!Guid.TryParse(value, out var id) || id == Guid.Empty)
            {
                errors.Add($"{prefix}.authorized_profile_ids values must be non-empty GUIDs.");
            }
            else if (!authorizedIds.Add(id))
            {
                errors.Add($"{prefix}.authorized_profile_ids must not contain duplicates.");
            }
        }
    }

    private static void ValidateLibrarySource(
        LibrarySourceConfig source,
        string prefix,
        HashSet<Guid> ids,
        List<(string Path, string Field)> normalizedPaths,
        List<string> errors)
    {
        if (!Guid.TryParse(source.Id, out var id) || id == Guid.Empty)
        {
            errors.Add($"{prefix}.id must be a non-empty GUID.");
        }
        else if (!ids.Add(id))
        {
            errors.Add($"{prefix}.id must be globally unique.");
        }

        AddRequired(errors, source.Path, $"{prefix}.path");
        if (TryNormalizePath(source.Path, out var normalizedPath))
        {
            normalizedPaths.Add((normalizedPath, $"{prefix}.path"));
        }
        else if (!string.IsNullOrWhiteSpace(source.Path))
        {
            errors.Add($"{prefix}.path must be an absolute path.");
        }

        if (!LibrarySourceRoles.IsValid(source.Role))
        {
            errors.Add($"{prefix}.role is unsupported.");
        }

        if (!LibrarySourceManagementModes.IsValid(source.ManagementMode))
        {
            errors.Add($"{prefix}.management_mode must be managed_by_tuvima or existing_library.");
        }

        if (!LibrarySourceTypes.IsValid(source.SourceType))
        {
            errors.Add($"{prefix}.source_type is unsupported.");
        }

        if (!LibrarySourceAccessModes.IsValid(source.AccessMode))
        {
            errors.Add($"{prefix}.access_mode must be writable or read_only.");
        }

        if (!LibrarySourceIntakeRoles.IsValid(source.IntakeRole))
        {
            errors.Add($"{prefix}.intake_role is unsupported.");
        }

        if (source.ManagementMode == LibrarySourceManagementModes.ExistingLibrary)
        {
            if (source.AccessMode != LibrarySourceAccessModes.ReadOnly)
            {
                errors.Add($"{prefix}.existing libraries must use read_only access.");
            }

            if (source.ParticipatesInOrganization)
            {
                errors.Add($"{prefix}.existing libraries cannot participate in organization.");
            }

            if (source.WritebackOverride == true)
            {
                errors.Add($"{prefix}.existing libraries cannot enable writeback.");
            }

            if (source.Role == LibrarySourceRoles.PrimaryDestination)
            {
                errors.Add($"{prefix}.existing libraries cannot be a primary destination.");
            }
        }

        if (source.Role == LibrarySourceRoles.PrimaryDestination
            && (source.ManagementMode != LibrarySourceManagementModes.ManagedByTuvima
                || source.AccessMode != LibrarySourceAccessModes.Writable))
        {
            errors.Add($"{prefix}.primary destinations must be managed and writable.");
        }

        if (!string.IsNullOrWhiteSpace(source.ProfileId)
            && (!Guid.TryParse(source.ProfileId, out var profileId) || profileId == Guid.Empty))
        {
            errors.Add($"{prefix}.profile_id must be a non-empty GUID.");
        }

        AddNoUnmappedProperties(source.UnmappedProperties, prefix, errors);
    }

    private static void ValidatePrimaryDestination(LibraryFolderConfig library, string prefix, List<string> errors)
    {
        var primaryRoles = library.Sources.Where(source => source.Role == LibrarySourceRoles.PrimaryDestination).ToList();
        var hasManagedSource = library.Sources.Any(source => source.ManagementMode == LibrarySourceManagementModes.ManagedByTuvima);
        if (!hasManagedSource)
        {
            if (!string.IsNullOrWhiteSpace(library.PrimaryDestinationSourceId) || primaryRoles.Count > 0)
            {
                errors.Add($"{prefix} cannot define a primary destination without a managed source.");
            }

            return;
        }

        if (!Guid.TryParse(library.PrimaryDestinationSourceId, out var primaryId) || primaryId == Guid.Empty)
        {
            errors.Add($"{prefix}.primary_destination_source_id must identify the managed primary source.");
            return;
        }

        var selected = library.Sources.FirstOrDefault(source =>
            Guid.TryParse(source.Id, out var sourceId) && sourceId == primaryId);
        if (selected is null)
        {
            errors.Add($"{prefix}.primary_destination_source_id must reference a source in this library.");
        }
        else if (selected.Role != LibrarySourceRoles.PrimaryDestination)
        {
            errors.Add($"{prefix}.primary_destination_source_id must reference the source with role primary_destination.");
        }

        if (primaryRoles.Count != 1)
        {
            errors.Add($"{prefix}.sources must contain exactly one primary_destination role when managed sources exist.");
        }
    }

    private static void ValidateOrganizationPolicy(
        LibraryOrganizationPolicyConfig policy,
        string prefix,
        List<string> errors)
    {
        if (!LibraryOrganizationModes.IsValid(policy.Mode))
        {
            errors.Add($"{prefix}.mode is unsupported.");
        }

        if (policy.Mode == LibraryOrganizationModes.Custom)
        {
            AddRequired(errors, policy.CustomTemplate, $"{prefix}.custom_template");
        }
        else if (!string.IsNullOrWhiteSpace(policy.CustomTemplate))
        {
            errors.Add($"{prefix}.custom_template is only valid when mode is custom.");
        }

        AddNoUnmappedProperties(policy.UnmappedProperties, prefix, errors);
    }

    private static bool TryNormalizePath(string path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        try
        {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool PathsOverlap(string first, string second)
    {
        if (string.Equals(first, second, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var separator = Path.DirectorySeparatorChar.ToString();
        return first.StartsWith(second + separator, StringComparison.OrdinalIgnoreCase)
            || second.StartsWith(first + separator, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddNoUnmappedProperties(
        Dictionary<string, System.Text.Json.JsonElement>? properties,
        string prefix,
        List<string> errors)
    {
        foreach (var property in properties?.Keys ?? Enumerable.Empty<string>())
        {
            errors.Add($"{prefix}.{property} is not supported by libraries schema 6.0.");
        }
    }

    private static void ValidateProvider(ProviderConfiguration provider, string relativePath, List<string> errors)
    {
        AddRequired(errors, provider.Name, "name");
        if (!string.IsNullOrWhiteSpace(provider.Name))
        {
            var fileName = Path.GetFileNameWithoutExtension(relativePath);
            if (!string.Equals(fileName, provider.Name, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("name must match the provider config filename.");
            }
        }

        AddRange(errors, provider.Weight, "weight", 0, 1);
        AddPositiveOrZero(errors, provider.ThrottleMs, "throttle_ms");
        AddPositive(errors, provider.MaxConcurrency, "max_concurrency");
        foreach (var stage in provider.HydrationStages)
        {
            if (stage is < 1 or > 3)
            {
                errors.Add("hydration_stages values must be 1, 2, or 3.");
            }
        }

        if (provider.HttpClient is not null)
        {
            AddPositive(errors, provider.HttpClient.TimeoutSeconds, "http_client.timeout_seconds");
        }

        if (provider.SequenceManifest?.Enabled == true)
        {
            AddRequired(errors, provider.SequenceManifest.UrlTemplate, "sequence_manifest.url_template");
            AddRequired(errors, provider.SequenceManifest.ContainerKind, "sequence_manifest.container_kind");
            AddRequired(errors, provider.SequenceManifest.ExpectedTotalKind, "sequence_manifest.expected_total_kind");
            AddPositive(errors, provider.SequenceManifest.PageSize, "sequence_manifest.page_size");
            AddPositive(errors, provider.SequenceManifest.MaxPages, "sequence_manifest.max_pages");
            if (provider.SequenceManifest.Fields.Count == 0)
            {
                errors.Add("sequence_manifest.fields must contain at least one field.");
            }

            if (provider.SequenceManifest.Fields.Any(field => field.Contains("image", StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add("sequence_manifest.fields must not request image fields.");
            }
        }

        var strategyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var strategy in provider.SearchStrategies ?? [])
        {
            AddRequired(errors, strategy.Name, "search_strategies[].name");
            AddPositiveOrZero(errors, strategy.Priority, "search_strategies[].priority");
            AddRequired(errors, strategy.UrlTemplate, "search_strategies[].url_template");
            if (!strategyNames.Add(strategy.Name))
            {
                errors.Add("search_strategies[].name values must be unique.");
            }

            if (strategy.Query is not null && !string.IsNullOrWhiteSpace(strategy.QueryTemplate))
            {
                errors.Add($"search_strategies['{strategy.Name}'] cannot define both query and query_template.");
            }

            if (strategy.Query is not null)
            {
                if (!Allowed(strategy.Query.Syntax, "plain", "lucene"))
                {
                    errors.Add($"search_strategies['{strategy.Name}'].query.syntax must be plain or lucene.");
                }

                if (!Allowed(strategy.Query.Operator, "AND", "OR"))
                {
                    errors.Add($"search_strategies['{strategy.Name}'].query.operator must be AND or OR.");
                }

                if (strategy.Query.Clauses.Count == 0)
                {
                    errors.Add($"search_strategies['{strategy.Name}'].query.clauses must not be empty.");
                }

                foreach (var clause in strategy.Query.Clauses)
                {
                    AddRequired(errors, clause.Value, $"search_strategies['{strategy.Name}'].query.clauses[].value");
                    if (!Allowed(clause.Match, "term", "phrase"))
                    {
                        errors.Add($"search_strategies['{strategy.Name}'].query.clauses[].match must be term or phrase.");
                    }
                }
            }

            ValidateCandidateSelection(strategy.CandidateSelection, strategy.Name, errors);
            ValidateRequestFilters(strategy.ReleaseSelection?.RequestFilters, strategy.Name, errors);
        }

        ValidateProviderOnboarding(provider.Onboarding, errors);
    }

    private static void ValidateProviderOnboarding(
        ProviderOnboardingConfiguration? onboarding,
        List<string> errors)
    {
        if (onboarding is null)
        {
            return;
        }

        if (!Allowed(onboarding.Classification, "built_in", "recommended", "optional"))
        {
            errors.Add("onboarding.classification is unsupported.");
        }

        if (onboarding.Intro is not null)
        {
            AddRequired(errors, onboarding.Intro.Title, "onboarding.intro.title");
            AddRequired(errors, onboarding.Intro.Summary, "onboarding.intro.summary");
        }

        var credentialKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var credential in onboarding.Credentials)
        {
            AddRequired(errors, credential.Key, "onboarding.credentials[].key");
            AddRequired(errors, credential.Label, "onboarding.credentials[].label");
            if (!credentialKeys.Add(credential.Key))
            {
                errors.Add("onboarding.credentials[].key values must be unique.");
            }

            if (!Allowed(credential.Ownership, "user_supplied", "application_managed"))
            {
                errors.Add($"onboarding.credentials['{credential.Key}'].ownership is unsupported.");
            }

            var purpose = string.IsNullOrWhiteSpace(credential.Purpose)
                ? credential.Key.ToLowerInvariant() switch
                {
                    "api_key" or "client_key" or "username" or "password" or "access_token" => credential.Key,
                    _ => "other",
                }
                : credential.Purpose;
            if (!Allowed(purpose, "api_key", "client_key", "username", "password", "access_token", "other"))
            {
                errors.Add($"onboarding.credentials['{credential.Key}'].purpose is unsupported.");
            }
        }

        var stepIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in onboarding.Steps)
        {
            AddRequired(errors, step.Id, "onboarding.steps[].id");
            AddRequired(errors, step.Title, "onboarding.steps[].title");
            if (!stepIds.Add(step.Id))
            {
                errors.Add("onboarding.steps[].id values must be unique.");
            }

            if (step.Action is not null
                && !Allowed(step.Action.Kind, "external_link", "credential_entry", "credential_probe", "information"))
            {
                errors.Add($"onboarding.steps['{step.Id}'].action.kind is unsupported.");
            }
            foreach (var key in step.CredentialKeys.Where(key => !credentialKeys.Contains(key)))
            {
                errors.Add($"onboarding.steps['{step.Id}'].credential_keys references unknown credential '{key}'.");
            }
        }

        foreach (var item in onboarding.Troubleshooting)
        {
            AddRequired(errors, item.Status, "onboarding.troubleshooting[].status");
            AddRequired(errors, item.Title, "onboarding.troubleshooting[].title");
            AddRequired(errors, item.Message, "onboarding.troubleshooting[].message");
        }
    }

    private static void ValidateCandidateSelection(
        CandidateSelectionConfig? selection,
        string strategyName,
        List<string> errors)
    {
        if (selection is null)
        {
            return;
        }

        if (selection.TitlePaths.Count == 0)
        {
            errors.Add($"search_strategies['{strategyName}'].candidate_selection.title_paths must not be empty.");
        }

        AddRange(errors, selection.MinimumTitleScore,
            $"search_strategies['{strategyName}'].candidate_selection.minimum_title_score", 0, 1);
        AddRange(errors, selection.MinimumCreatorScore,
            $"search_strategies['{strategyName}'].candidate_selection.minimum_creator_score", 0, 1);
        ValidateRequestFilters(selection.RequestFilters, strategyName, errors);
    }

    private static void ValidateRequestFilters(
        IReadOnlyList<RequestCandidateFilterConfig>? filters,
        string strategyName,
        List<string> errors)
    {
        foreach (var filter in filters ?? [])
        {
            AddRequired(errors, filter.RequestField,
                $"search_strategies['{strategyName}'].request_filters[].request_field");
            if (filter.CandidatePaths.Count == 0)
            {
                errors.Add($"search_strategies['{strategyName}'].request_filters[].candidate_paths must not be empty.");
            }

            if (!Allowed(filter.Operator, "exact", "normalized_similarity", "album_identity"))
            {
                errors.Add($"search_strategies['{strategyName}'].request_filters[].operator is unsupported.");
            }

            AddRange(errors, filter.MinimumScore,
                $"search_strategies['{strategyName}'].request_filters[].minimum_score", 0, 1);
        }
    }

    private static void ValidateMediaTypes(MediaTypeConfiguration mediaTypes, List<string> errors)
    {
        AddRequired(errors, mediaTypes.Version, "version");
        if (mediaTypes.Types.Count == 0)
        {
            errors.Add("types must contain at least one media type.");
        }

        foreach (var type in mediaTypes.Types)
        {
            AddRequired(errors, type.Key, "types[].key");
            AddRequired(errors, type.DisplayName, "types[].display_name");
            AddRequired(errors, type.CategoryFolder, "types[].category_folder");
            if (type.Extensions.Any(extension => !extension.StartsWith('.')))
            {
                errors.Add("types[].extensions values must start with '.'.");
            }
        }
    }

    private static void ValidatePipelines(Dictionary<string, MediaTypePipeline> pipelines, List<string> errors)
    {
        foreach (var (mediaType, pipeline) in pipelines)
        {
            AddRequired(errors, mediaType, "pipeline media type key");
            AddPositive(errors, pipeline.MaxProviderAttempts, $"{mediaType}.max_provider_attempts");
            if (!Allowed(pipeline.Scoring.CreatorListMode, "proportional", "local-primary-containment"))
            {
                errors.Add($"{mediaType}.scoring.creator_list_mode is unsupported.");
            }

            if (pipeline.Scoring.AutoAcceptThreshold is { } autoAccept)
            {
                AddRange(errors, autoAccept, $"{mediaType}.scoring.auto_accept_threshold", 0, 1);
            }

            if (pipeline.Scoring.AmbiguousThreshold is { } ambiguous)
            {
                AddRange(errors, ambiguous, $"{mediaType}.scoring.ambiguous_threshold", 0, 1);
            }

            if (pipeline.Scoring.AutoAcceptThreshold is { } accept
                && pipeline.Scoring.AmbiguousThreshold is { } review
                && review >= accept)
            {
                errors.Add($"{mediaType}.scoring.ambiguous_threshold must be lower than auto_accept_threshold.");
            }

            var requiredScopes = mediaType switch
            {
                "TV" => new[] { "series", "episode" },
                "Music" => new[] { "track", "album" },
                "Comics" => new[] { "issue" },
                "Books" or "Audiobooks" or "Movies" => new[] { "default" },
                _ => Array.Empty<string>(),
            };
            foreach (var requiredScope in requiredScopes)
                if (!pipeline.Scoring.Scopes.ContainsKey(requiredScope))
                    errors.Add($"{mediaType}.scoring.scopes.{requiredScope} is required.");
            foreach (var (scopeKey, scope) in pipeline.Scoring.Scopes)
            {
                var prefix = $"{mediaType}.scoring.scopes.{scopeKey}";
                if (!Allowed(scopeKey, "default", "series", "episode", "track", "album", "issue"))
                    errors.Add($"{prefix} is unsupported.");
                if (scope.Fields.Count == 0 || Math.Abs(scope.Fields.Values.Sum(field => field.Weight) - 1) > .001)
                    errors.Add($"{prefix}.fields weights must sum to 1.");
                foreach (var (fieldKey, field) in scope.Fields)
                {
                    if (!Allowed(fieldKey, "title", "author", "year", "narrator", "season_episode", "album", "duration", "track_count", "series", "issue"))
                        errors.Add($"{prefix}.fields.{fieldKey} is unsupported; genre is never scored.");
                    AddRange(errors, field.Weight, $"{prefix}.fields.{fieldKey}.weight", 0, 1);
                    if (!double.IsFinite(field.Weight)) errors.Add($"{prefix}.fields.{fieldKey}.weight must be finite.");
                    if (!Allowed(field.IfMissing, "zero", "redistribute", "zero-if-file-has"))
                        errors.Add($"{prefix}.fields.{fieldKey}.if_missing is unsupported.");
                }
                if (!scope.Gates.Contains("format", StringComparer.Ordinal)) errors.Add($"{prefix} requires the format gate.");
                foreach (var gate in scope.Gates)
                    if (!Allowed(gate, "format", "not_derivative", "show_title")) errors.Add($"{prefix}.gates.{gate} is unsupported.");
                foreach (var signals in new[] { scope.Bonuses, scope.Penalties })
                    foreach (var (signal, value) in signals)
                    {
                        if (!Allowed(signal, "exact_id", "cover", "publisher", "page_count", "series_description", "duration", "director", "year", "runtime", "episode", "season", "track_disc", "writer", "language"))
                            errors.Add($"{prefix}.{signal} is unsupported; genre is never scored.");
                        if (!double.IsFinite(value) || value < 0 || value > 1) errors.Add($"{prefix}.{signal} must be finite and between 0 and 1.");
                    }
            }

            var ranks = new HashSet<int>();
            foreach (var provider in pipeline.Providers)
            {
                AddPositive(errors, provider.Rank, $"{mediaType}.providers[].rank");
                AddRequired(errors, provider.Name, $"{mediaType}.providers[].name");
                AddRequired(errors, provider.Purpose, $"{mediaType}.providers[].purpose");
                if (provider.Purpose is not null
                    && provider.Purpose is not ("identity" or "enrichment" or "retail" or "artwork" or "text-track" or "canonical"))
                {
                    errors.Add($"{mediaType}.providers[].purpose has unsupported value '{provider.Purpose}'.");
                }
                if (provider.RequiresIdentity
                    && !string.Equals(provider.Purpose, "enrichment", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"{mediaType}.providers[].requires_identity is only valid for enrichment providers.");
                }
                if (provider.UseAsIdentityFallback
                    && !string.Equals(provider.Purpose, "enrichment", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"{mediaType}.providers[].use_as_identity_fallback is only valid for enrichment providers.");
                }

                if (provider.AcceptedTransition is { } transition)
                {
                    AddRequired(errors, transition.Provider, $"{mediaType}.providers[].accepted_transition.provider");
                    AddPositive(errors, transition.MaxAttempts, $"{mediaType}.providers[].accepted_transition.max_attempts");
                    if (!Allowed(transition.When, "accepted", "identity-fallback-accepted"))
                    {
                        errors.Add($"{mediaType}.providers[].accepted_transition.when is unsupported.");
                    }

                    if (transition.HintFields.Count == 0)
                    {
                        errors.Add($"{mediaType}.providers[].accepted_transition.hint_fields must not be empty.");
                    }
                }
                foreach (var action in provider.AcceptedActions)
                {
                    if (!Allowed(action, "apple-album-manifest"))
                    {
                        errors.Add($"{mediaType}.providers[].accepted_actions contains unsupported value '{action}'.");
                    }
                }
                if (!ranks.Add(provider.Rank))
                {
                    errors.Add($"{mediaType}.providers rank values must be unique.");
                }
            }

            var ordered = pipeline.Providers.OrderBy(provider => provider.Rank).ToList();
            foreach (var provider in ordered.Where(provider => provider.RequiresIdentity))
            {
                if (!provider.UseAsIdentityFallback
                    && !ordered.Any(candidate => candidate.Rank < provider.Rank
                    && string.Equals(candidate.Purpose, "identity", StringComparison.OrdinalIgnoreCase)))
                {
                    errors.Add($"{mediaType}.providers enrichment provider '{provider.Name}' requires an earlier identity provider.");
                }
            }

            foreach (var provider in ordered.Where(provider => provider.AcceptedTransition is not null))
            {
                var transition = provider.AcceptedTransition!;
                if (!pipeline.Providers.Any(candidate =>
                    string.Equals(candidate.Name, transition.Provider, StringComparison.OrdinalIgnoreCase)))
                {
                    errors.Add($"{mediaType}.providers transition target '{transition.Provider}' is not configured in the pipeline.");
                }

                if (transition.MaxAttempts >= pipeline.MaxProviderAttempts)
                {
                    errors.Add($"{mediaType}.providers transition max_attempts must be lower than max_provider_attempts.");
                }
            }
        }
    }

    private static void ValidatePalette(PaletteConfiguration palette, List<string> errors)
    {
        ValidateColorMap(palette.Theme, "theme", errors);
        ValidateColorMap(palette.Status, "status", errors);
        ValidateColorMap(palette.Pipeline, "pipeline", errors);
        ValidateColorMap(palette.MediaType, "media_type", errors);
        ValidateColorMap(palette.Confidence, "confidence", errors);
        ValidateColorMap(palette.ReviewTrigger, "review_trigger", errors);
    }

    private static void ValidateLibraryPreferences(LibraryPreferencesSettings preferences, List<string> errors)
    {
        var requiredMediaTypes = Enum.GetValues<MediaType>()
            .Where(mediaType => mediaType != MediaType.Unknown)
            .Select(mediaType => mediaType.ToString().ToLowerInvariant())
            .ToArray();
        var unknown = preferences.MissingItemDisplay.Keys
            .Except(requiredMediaTypes, StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var key in unknown)
        {
            errors.Add($"missing_item_display contains unknown media type '{key}'.");
        }

        foreach (var mediaType in requiredMediaTypes)
        {
            if (!preferences.MissingItemDisplay.TryGetValue(mediaType, out var policy))
            {
                errors.Add($"missing_item_display.{mediaType} is required.");
                continue;
            }

            if (!Allowed(policy.DefaultVisibility, "shown", "hidden"))
            {
                errors.Add($"missing_item_display.{mediaType}.default_visibility must be shown or hidden.");
            }

            if (!Allowed(policy.Presentation, "all", "paged"))
            {
                errors.Add($"missing_item_display.{mediaType}.presentation must be all or paged.");
            }

            if (!Allowed(policy.DetailHydration, "owned_only", "on_demand", "all"))
            {
                errors.Add($"missing_item_display.{mediaType}.detail_hydration must be owned_only, on_demand, or all.");
            }

            if (policy.PageSize is < 1 or > 500)
            {
                errors.Add($"missing_item_display.{mediaType}.page_size must be between 1 and 500.");
            }
        }
    }

    private static void ValidateColorMap(object colors, string section, List<string> errors)
    {
        foreach (var property in colors.GetType().GetProperties())
        {
            if (property.GetValue(colors) is not string value)
            {
                continue;
            }

            var name = property.Name;
            if (string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"{section}.{name} must not be empty.");
            }
            else if (!value.StartsWith("rgba(", StringComparison.OrdinalIgnoreCase) && !HexColorRegex.IsMatch(value))
            {
                errors.Add($"{section}.{name} must be a hex color or rgba() value.");
            }
        }
    }

    private static bool Allowed(string value, params string[] allowed) =>
        allowed.Contains(value, StringComparer.OrdinalIgnoreCase);

    private static void AddRequired(List<string> errors, string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{field} is required.");
        }
    }

    private static void AddPositive(List<string> errors, int value, string field)
    {
        if (value <= 0)
        {
            errors.Add($"{field} must be greater than 0.");
        }
    }

    private static void AddPositiveOrZero(List<string> errors, int value, string field)
    {
        if (value < 0)
        {
            errors.Add($"{field} must be 0 or greater.");
        }
    }

    private static void AddRange(List<string> errors, double value, string field, double min, double max)
    {
        if (value < min || value > max)
        {
            errors.Add($"{field} must be between {min} and {max}.");
        }
    }
}
