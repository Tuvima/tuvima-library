using MediaEngine.Api.Http;
using MediaEngine.Api.Models;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Playback;
using MediaEngine.Contracts.Profiles;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Identity.Contracts;
using MediaEngine.Storage.Contracts;
using Microsoft.AspNetCore.Mvc;
using SkiaSharp;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// Profile management endpoints.
///
/// Routes under <c>/profiles</c>:
///   GET    /profiles         — list all profiles
///   GET    /profiles/{id}    — get a single profile
///   POST   /profiles         — create a new profile
///   PUT    /profiles/{id}    — update an existing profile
///   DELETE /profiles/{id}    — delete a profile (cannot delete seed or last admin)
///
/// Spec: Settings & Management Layer — Identity & Multi-User.
/// </summary>
public static class ProfileEndpoints
{
    public static IEndpointRouteBuilder MapProfileEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/profiles").WithTags("Profiles");

        group.MapPut("/{id:guid}/experience", UpdateExperienceAsync)
            .WithName("UpdateProfileExperience")
            .WithSummary("Updates the active profile's name, avatar color, and navigation preferences.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .RequireActiveProfile();

        group.MapGet("/", async (
            HttpContext http,
            IRequestAuthorityResolver resolver,
            IProfileService svc,
            IAccountRepository accounts,
            [FromServices] IFirstPartyIdentityService identity,
            CancellationToken ct) =>
        {
            var authority = await resolver.ResolveAsync(http, ct);
            var profiles = await svc.GetAllProfilesAsync(ct);
            var allowed = (await accounts.GetProfileIdsAsync(authority.AccountId!.Value, ct)).ToHashSet();
            profiles = ProfilesForCaller(
                await accounts.GetByIdAsync(authority.AccountId!.Value, ct), profiles, allowed);
            var withPin = await identity.GetProfileIdsWithPinAsync([.. profiles.Select(profile => profile.Id)], ct);
            var dtos = profiles.Select(profile => ProfileContractMapper.ToResponse(profile, withPin.Contains(profile.Id))).ToList();
            return Results.Ok(dtos);
        })
        .WithName("ListProfiles")
        .WithSummary("List all user profiles.")
        .Produces<List<ProfileResponseDto>>(StatusCodes.Status200OK)
        .RequireHumanSelfService();

        group.MapGet("/{id:guid}", async (
            Guid id,
            IProfileService svc,
            CancellationToken ct) =>
        {
            var profile = await svc.GetProfileAsync(id, ct);
            return profile is null
                ? ApiErrors.NotFound($"Profile '{id}' not found.")
                : Results.Ok(ProfileContractMapper.ToResponse(profile));
        })
        .WithName("GetProfile")
        .WithSummary("Get a single profile by ID.")
        .Produces<ProfileResponseDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireActiveProfile();

        group.MapGet("/{id:guid}/overview", async (
            Guid id,
            IProfileOverviewReadService overviewReadService,
            CancellationToken ct) =>
        {
            var response = await overviewReadService.GetOverviewAsync(id, ct);
            if (response is null)
            {
                return ApiErrors.NotFound($"Profile '{id}' not found.");
            }

            return Results.Ok(response);
        })
        .WithName("GetProfileOverview")
        .WithSummary("Get user-facing profile details, history, statistics, and taste signals.")
        .Produces<ProfileOverviewResponseDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireActiveProfile();

        group.MapGet("/{id:guid}/settings/playback", async (
            Guid id,
            IUserPlaybackSettingsService settingsService,
            CancellationToken ct) =>
        {
            try
            {
                var settings = await settingsService.GetOrCreateDefaultsAsync(id, ct);
                return Results.Ok(settings);
            }
            catch (KeyNotFoundException ex)
            {
                return ApiErrors.NotFound(ex.Message);
            }
        })
        .WithName("GetProfilePlaybackSettings")
        .WithSummary("Get user playback and reading settings for a profile (the person themselves, or an administrator of their household).")
        .Produces<UserPlaybackSettingsDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireProfileSettingsAccess();

        group.MapGet("/{id:guid}/settings/view", async (
            Guid id,
            HttpContext http,
            IRequestAuthorityResolver resolver,
            IAccountRepository accounts,
            IProfileService profileService,
            IViewProfileRepository viewProfiles,
            CancellationToken ct) =>
        {
            var profile = await profileService.GetProfileAsync(id, ct);
            if (profile is null)
            {
                return ApiErrors.NotFound($"Profile '{id}' not found.");
            }

            if (!await MayManageViewPolicyAsync(http, resolver, accounts, profile, ct))
            {
                return ApiErrors.Forbidden("Only a server administrator can manage this profile.");
            }

            var policy = await viewProfiles.GetPolicyAsync(id, ct);
            return Results.Ok(ProfileContractMapper.ToResponse(policy));
        })
        .WithName("GetViewProfilePolicy")
        .WithSummary("Get the View access policy for a profile (server administrators, or household administrators for their own household).")
        .Produces<ViewProfilePolicyDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireEffectiveAdministratorOrHouseholdAdministrator();

        group.MapPut("/{id:guid}/settings/view", async (
            Guid id,
            UpdateViewProfilePolicyRequest request,
            HttpContext http,
            IRequestAuthorityResolver resolver,
            IAccountRepository accounts,
            IProfileService profileService,
            IViewProfileRepository viewProfiles,
            MediaEngine.Api.Services.LocalAssets.ViewStorageService viewStorage,
            CancellationToken ct) =>
        {
            var profile = await profileService.GetProfileAsync(id, ct);
            if (profile is null)
            {
                return ApiErrors.NotFound($"Profile '{id}' not found.");
            }

            if (!await MayManageViewPolicyAsync(http, resolver, accounts, profile, ct))
            {
                return ApiErrors.Forbidden("Only a server administrator can manage this profile.");
            }

            // A request that leaves allow_documents out keeps the saved switch (a child profile's default is off).
            var policy = ProfileContractMapper.ToDomain(
                id, request, (await viewProfiles.GetPolicyAsync(id, ct)).DocumentsEnabled);
            if (!await viewProfiles.SavePolicyAsync(policy, ct))
            {
                return ApiErrors.NotFound($"Profile '{id}' not found.");
            }

            if (policy.ViewEnabled)
            {
                await viewStorage.EnsurePersonalSpaceAsync(id, ct);
            }

            return Results.Ok(ProfileContractMapper.ToResponse(
                await viewProfiles.GetPolicyAsync(id, ct)));
        })
        .WithName("UpdateViewProfilePolicy")
        .WithSummary("Update View, Shared Library contribution, and Gallery-sharing permissions for a profile.")
        .Produces<ViewProfilePolicyDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireEffectiveAdministratorOrHouseholdAdministrator();

        group.MapPut("/{id:guid}/settings/playback", async (
            Guid id,
            UserPlaybackSettingsDto request,
            IUserPlaybackSettingsService settingsService,
            CancellationToken ct) =>
        {
            try
            {
                var saved = await settingsService.UpdateAsync(id, request, ct);
                return Results.Ok(saved);
            }
            catch (KeyNotFoundException ex)
            {
                return ApiErrors.NotFound(ex.Message);
            }
            catch (ArgumentException ex)
            {
                return ApiErrors.BadRequest(ex.Message);
            }
        })
        .WithName("UpdateProfilePlaybackSettings")
        .WithSummary("Save user playback and reading settings for a profile (the person themselves, or an administrator of their household).")
        .Produces<UserPlaybackSettingsDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireProfileSettingsAccess();

        group.MapGet("/{id:guid}/settings/spoilers", async (
            Guid id,
            IUserPlaybackSettingsService settingsService,
            CancellationToken ct) =>
        {
            try
            {
                var level = await settingsService.GetSpoilerLevelAsync(id, ct);
                return Results.Ok(new ProfileSpoilerPreferenceDto
                {
                    ProfileId = id,
                    SpoilerLevel = SpoilerLevels.ToWire(level),
                });
            }
            catch (KeyNotFoundException ex)
            {
                return ApiErrors.NotFound(ex.Message);
            }
        })
        .WithName("GetProfileSpoilerPreference")
        .WithSummary("Get how much of a story characters may reveal for the signed-in profile (up_to_progress by default). Only the person themselves; another profile is not found.")
        .Produces<ProfileSpoilerPreferenceDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireActiveProfile();

        group.MapPut("/{id:guid}/settings/spoilers", async (
            Guid id,
            SaveProfileSpoilerPreferenceRequest request,
            IUserPlaybackSettingsService settingsService,
            CancellationToken ct) =>
        {
            try
            {
                var level = await settingsService.SetSpoilerLevelAsync(id, request.SpoilerLevel, ct);
                return Results.Ok(new ProfileSpoilerPreferenceDto
                {
                    ProfileId = id,
                    SpoilerLevel = SpoilerLevels.ToWire(level),
                });
            }
            catch (KeyNotFoundException ex)
            {
                return ApiErrors.NotFound(ex.Message);
            }
            catch (ArgumentException ex)
            {
                return ApiErrors.BadRequest(ex.Message);
            }
        })
        .WithName("SetProfileSpoilerPreference")
        .WithSummary("Choose how much of a story characters may reveal for the signed-in profile (up_to_progress or everything). Only the person themselves; another profile is not found.")
        .Produces<ProfileSpoilerPreferenceDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireActiveProfile();

        group.MapGet("/{id:guid}/sequence-preferences/missing-items", async (
            Guid id,
            string mediaType,
            string containerKey,
            IProfileService profileService,
            IProfileSequencePreferencesRepository preferences,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(mediaType) || string.IsNullOrWhiteSpace(containerKey))
            {
                return ApiErrors.BadRequest("mediaType and containerKey are required.");
            }

            if (await profileService.GetProfileAsync(id, ct) is null)
            {
                return ApiErrors.NotFound($"Profile '{id}' not found.");
            }

            var preference = await preferences.GetAsync(id, mediaType, containerKey, ct);
            return Results.Ok(ToSeriesMissingItemPreferenceDto(
                id,
                mediaType,
                containerKey,
                preference));
        })
        .WithName("GetProfileSeriesMissingItemPreference")
        .WithSummary("Get an explicit per-series missing-item visibility override. A null value inherits the media configuration default.")
        .Produces<SeriesMissingItemPreferenceDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireActiveProfile();

        group.MapPut("/{id:guid}/sequence-preferences/missing-items", async (
            Guid id,
            SaveSeriesMissingItemPreferenceRequest request,
            IProfileSequencePreferencesRepository preferences,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.MediaType) || string.IsNullOrWhiteSpace(request.ContainerKey))
            {
                return ApiErrors.BadRequest("media_type and container_key are required.");
            }

            var saved = await preferences.SaveAsync(
                id,
                request.MediaType,
                request.ContainerKey,
                request.ShowMissing,
                ct);
            if (!saved.ProfileExists || saved.Preference is null)
            {
                return ApiErrors.NotFound($"Profile '{id}' not found.");
            }

            return Results.Ok(ToSeriesMissingItemPreferenceDto(
                id,
                request.MediaType,
                request.ContainerKey,
                saved.Preference));
        })
        .WithName("SetProfileSeriesMissingItemPreference")
        .WithSummary("Save an explicit per-series missing-item visibility override for a profile.")
        .Produces<SeriesMissingItemPreferenceDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireActiveProfile();

        group.MapDelete("/{id:guid}/sequence-preferences/missing-items", async (
            Guid id,
            string mediaType,
            string containerKey,
            IProfileService profileService,
            IProfileSequencePreferencesRepository preferences,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(mediaType) || string.IsNullOrWhiteSpace(containerKey))
            {
                return ApiErrors.BadRequest("mediaType and containerKey are required.");
            }

            if (await profileService.GetProfileAsync(id, ct) is null)
            {
                return ApiErrors.NotFound($"Profile '{id}' not found.");
            }

            await preferences.DeleteAsync(id, mediaType, containerKey, ct);
            return Results.Ok(ToSeriesMissingItemPreferenceDto(
                id,
                mediaType,
                containerKey,
                preference: null));
        })
        .WithName("ResetProfileSeriesMissingItemPreference")
        .WithSummary("Remove a per-series override so the series inherits its media configuration default.")
        .Produces<SeriesMissingItemPreferenceDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireActiveProfile();

        group.MapGet("/{id:guid}/avatar", async (
            Guid id,
            IProfileService svc,
            CancellationToken ct) =>
        {
            var profile = await svc.GetProfileAsync(id, ct);
            if (profile is null)
            {
                return ApiErrors.NotFound($"Profile '{id}' not found.");
            }

            if (string.IsNullOrWhiteSpace(profile.AvatarImagePath) || !File.Exists(profile.AvatarImagePath))
            {
                return ApiErrors.NotFound("No avatar image has been uploaded.");
            }

            var bytes = await File.ReadAllBytesAsync(profile.AvatarImagePath, ct);
            return Results.File(bytes, GetAvatarMimeType(profile.AvatarImagePath), Path.GetFileName(profile.AvatarImagePath));
        })
        .WithName("GetProfileAvatar")
        .WithSummary("Serves a profile avatar image.")
        .Produces(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireGrantedProfileIdentity();

        group.MapPost("/{id:guid}/avatar", UploadProfileAvatarAsync)
        .WithName("UploadProfileAvatar")
        .WithSummary("Uploads and stores a profile avatar image.")
        .DisableAntiforgery()
        .Produces<ProfileResponseDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireActiveProfile();

        group.MapDelete("/{id:guid}/avatar", RemoveProfileAvatarAsync)
        .WithName("RemoveProfileAvatar")
        .WithSummary("Removes the uploaded avatar image for a profile.")
        .Produces<ProfileResponseDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireActiveProfile();

        return app;
    }

    /// <summary>
    /// The people a caller's account may open: its granted profiles, and for anyone but a server administrator only those
    /// in the account's own household (the same rule as switching person).
    /// </summary>
    public static IReadOnlyList<Profile> ProfilesForCaller(Account? account, IEnumerable<Profile> profiles, IReadOnlySet<Guid> granted) =>
        [.. profiles.Where(profile => granted.Contains(profile.Id) && account is not null && account.MaySwitchInto(profile.HouseholdId))];

    private static SeriesMissingItemPreferenceDto ToSeriesMissingItemPreferenceDto(
        Guid profileId,
        string mediaType,
        string containerKey,
        ProfileSequencePreference? preference) => new()
        {
            ProfileId = profileId,
            MediaType = preference?.MediaType ?? mediaType.Trim().ToLowerInvariant(),
            ContainerKey = preference?.ContainerKey ?? containerKey.Trim().ToLowerInvariant(),
            ShowMissing = preference?.ShowMissing,
            UpdatedAt = preference?.UpdatedAt,
        };

    /// <summary>
    /// A server administrator manages any profile's View policy; a household administrator only profiles in their
    /// own household (for example to let a child profile send items to the Shared Library).
    /// </summary>
    private static async Task<bool> MayManageViewPolicyAsync(
        HttpContext http, IRequestAuthorityResolver resolver, IAccountRepository accounts, Profile profile, CancellationToken ct)
    {
        var authority = await resolver.ResolveAsync(http, ct);
        if (authority.IsEffectiveAdministrator)
        {
            return true;
        }

        if (!authority.IsEffectiveHouseholdAdministrator
            || profile.HouseholdId is not { } household
            || household != authority.AccountHouseholdId)
        {
            return false;
        }

        // Like other household actions: not a person an administrator (server or household) other than the caller
        // signs in as, unless it is the caller's own active person.
        if (profile.Id == authority.ActiveProfileId)
        {
            return true;
        }

        foreach (var account in (await accounts.GetAllAsync(ct))
            .Where(account => account.HouseholdId == household && account.Id != authority.AccountId
                              && (account.IsAdministrator || account.HouseholdAdmin)))
        {
            if (await accounts.GetGrantAsync(account.Id, profile.Id, ct) is { IsEnabled: true } grant
                && (grant.AdminEnabled || grant.IsDefault))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A person manages their own settings; otherwise the caller must be a server administrator, or a household
    /// administrator for a profile in their own household (with the same carve-out as the View policy).
    /// </summary>
    internal static async Task<bool> MayManageProfileSettingsAsync(
        HttpContext http, IRequestAuthorityResolver resolver, IAccountRepository accounts, Profile profile, CancellationToken ct)
    {
        // Defence in depth: the route filter already lets the active profile through before reaching here.
        var authority = await resolver.ResolveAsync(http, ct);
        if (profile.Id == authority.ActiveProfileId)
        {
            return true;
        }

        return await MayManageViewPolicyAsync(http, resolver, accounts, profile, ct);
    }

    private static RouteHandlerBuilder RequireProfileSettingsAccess(this RouteHandlerBuilder builder) =>
        builder.RequireHumanSelfService()
            .AddEndpointFilter(async (context, next) =>
            {
                if (!Guid.TryParse(context.HttpContext.Request.RouteValues["id"]?.ToString(), out var profileId))
                {
                    return ApiErrors.NotFound("Profile not found.");
                }

                return await AuthorizeProfileSettingsAsync(context.HttpContext, profileId)
                    ?? await next(context);
            })
            .WithMetadata(new AuthorityRequirementMetadata("profile_settings_resource"));

    /// <summary>Returns null when the caller may read and write the person's settings, otherwise the refusal.</summary>
    internal static async Task<IResult?> AuthorizeProfileSettingsAsync(HttpContext http, Guid profileId)
    {
        if (await IsActiveProfileAuthorizedAsync(http, profileId))
        {
            return null;
        }

        const string denied = "Only an administrator of this household can change these settings.";
        var resolver = http.RequestServices.GetRequiredService<IRequestAuthorityResolver>();
        var decisions = http.RequestServices.GetRequiredService<IAccountAccessDecisionService>();
        var authority = await resolver.ResolveAsync(http, http.RequestAborted);

        // The administrator PIN unlock applies exactly as on the View policy routes.
        var isAdministrator = authority.IsEffectiveAdministrator
            ? (await decisions.EvaluateAdministratorAsync(authority, true, http.RequestAborted)).IsAllowed
            : (await decisions.EvaluateHouseholdAdministratorAsync(authority, true, http.RequestAborted)).IsAllowed;
        if (!isAdministrator)
        {
            return ApiErrors.Forbidden(denied);
        }

        var profile = await http.RequestServices.GetRequiredService<IProfileService>()
            .GetProfileAsync(profileId, http.RequestAborted);
        if (profile is null)
        {
            return ApiErrors.NotFound($"Profile '{profileId}' not found.");
        }

        var accounts = http.RequestServices.GetRequiredService<IAccountRepository>();
        return await MayManageProfileSettingsAsync(http, resolver, accounts, profile, http.RequestAborted)
            ? null
            : ApiErrors.Forbidden(denied);
    }

    private static RouteHandlerBuilder RequireActiveProfile(this RouteHandlerBuilder builder) =>
        builder.RequireHumanSelfService()
            .AddEndpointFilter(async (context, next) =>
            {
                if (!Guid.TryParse(
                        context.HttpContext.Request.RouteValues["id"]?.ToString(),
                        out var profileId))
                {
                    return ApiErrors.NotFound("Profile not found.");
                }

                return await IsActiveProfileAuthorizedAsync(context.HttpContext, profileId)
                    ? await next(context)
                    : ApiErrors.NotFound("Profile not found.");
            })
            .WithMetadata(new AuthorityRequirementMetadata("active_profile_resource"));

    private static RouteHandlerBuilder RequireGrantedProfileIdentity(this RouteHandlerBuilder builder) =>
        builder.RequireHumanSelfService()
            .AddEndpointFilter(async (context, next) =>
            {
                if (!Guid.TryParse(
                        context.HttpContext.Request.RouteValues["id"]?.ToString(),
                        out var profileId))
                {
                    return ApiErrors.NotFound("Profile not found.");
                }

                return await IsGrantedProfileIdentityAuthorizedAsync(context.HttpContext, profileId)
                    ? await next(context)
                    : ApiErrors.NotFound("Profile not found.");
            })
            .WithMetadata(new AuthorityRequirementMetadata("granted_profile_identity"));

    internal static async ValueTask<bool> IsActiveProfileAuthorizedAsync(
        HttpContext context,
        Guid profileId)
    {
        var resolver = context.RequestServices.GetRequiredService<IRequestAuthorityResolver>();
        var decisions = context.RequestServices.GetRequiredService<ISelfServiceAuthorizationService>();
        var authority = await resolver.ResolveAsync(context, context.RequestAborted);
        return (await decisions.EvaluateProfileAsync(
            authority,
            profileId,
            context.RequestAborted)).IsAllowed;
    }

    internal static async ValueTask<bool> IsGrantedProfileIdentityAuthorizedAsync(
        HttpContext context,
        Guid profileId)
    {
        var resolver = context.RequestServices.GetRequiredService<IRequestAuthorityResolver>();
        var authority = await resolver.ResolveAsync(context, context.RequestAborted);
        if (authority.PrincipalKind != PrincipalKind.Human ||
            !authority.AccountEnabled || !authority.GrantEnabled ||
            authority.AccountId is not { } accountId)
        {
            return false;
        }

        var accounts = context.RequestServices.GetRequiredService<IAccountRepository>();
        return await accounts.HasProfileAccessAsync(accountId, profileId, context.RequestAborted);
    }

    /// <summary>Removes a profile's uploaded photo. The caller has already decided the person may change it.</summary>
    internal static async Task<IResult> RemoveProfileAvatarAsync(
        Guid id,
        IProfileService svc,
        CancellationToken ct)
    {
        var profile = await svc.GetProfileAsync(id, ct);
        if (profile is null)
        {
            return ApiErrors.NotFound($"Profile '{id}' not found.");
        }

        var existingPath = profile.AvatarImagePath;
        profile.AvatarImagePath = null;
        var updated = await svc.UpdateProfileAsync(profile, ct);
        if (!updated)
        {
            return Results.Problem("Could not remove profile avatar.");
        }

        if (!string.IsNullOrWhiteSpace(existingPath) && File.Exists(existingPath))
        {
            File.Delete(existingPath);
        }

        return Results.Ok(ProfileContractMapper.ToResponse(profile));
    }

    internal static async Task<IResult> UploadProfileAvatarAsync(
        Guid id,
        HttpRequest request,
        IProfileService svc,
        TuvimaDataPaths dataPaths,
        CancellationToken ct)
    {
        var profile = await svc.GetProfileAsync(id, ct);
        if (profile is null)
        {
            return ApiErrors.NotFound($"Profile '{id}' not found.");
        }

        if (!request.HasFormContentType)
        {
            return ApiErrors.BadRequest("Expected multipart form data.");
        }

        var form = await request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        if (file is null || file.Length == 0)
        {
            return ApiErrors.BadRequest("No file uploaded.");
        }

        if (file.Length > 5 * 1024 * 1024)
        {
            return ApiErrors.BadRequest("Avatar image must be 5 MB or smaller.");
        }

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        var mimeType = NormalizeAvatarMimeType(file.ContentType, extension);
        if (mimeType is null)
        {
            return ApiErrors.BadRequest("Avatar image must be a JPEG, PNG, or WebP image.");
        }

        var zoom = ParseAvatarZoom(form.TryGetValue("zoom", out var zoomValue) ? zoomValue.ToString() : null);

        dataPaths.EnsureRootExists();
        var directory = Path.Combine(dataPaths.Root, "profiles", id.ToString("D"));
        Directory.CreateDirectory(directory);
        var replacementPath = Path.Combine(directory, $"avatar-{Guid.NewGuid():N}{extension}");
        var existingPath = profile.AvatarImagePath;
        var committed = false;

        try
        {
            await using var upload = file.OpenReadStream();
            await SaveAvatarImageAsync(upload, replacementPath, extension, zoom, ct);

            profile.AvatarImagePath = replacementPath;
            if (!await svc.UpdateProfileAsync(profile, ct))
            {
                return Results.Problem("Could not update profile avatar.");
            }

            committed = true;
        }
        catch (ArgumentException ex)
        {
            return ApiErrors.BadRequest(ex.Message);
        }
        finally
        {
            if (!committed)
            {
                profile.AvatarImagePath = existingPath;
                if (File.Exists(replacementPath))
                {
                    File.Delete(replacementPath);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(existingPath)
            && !string.Equals(existingPath, replacementPath, StringComparison.OrdinalIgnoreCase)
            && File.Exists(existingPath))
        {
            File.Delete(existingPath);
        }

        return Results.Ok(ProfileContractMapper.ToResponse(profile));
    }

    private static string? NormalizeAvatarMimeType(string? contentType, string extension)
    {
        var normalized = contentType?.Trim().ToLowerInvariant();
        if (normalized is "image/jpeg" or "image/jpg")
        {
            return "image/jpeg";
        }

        if (normalized is "image/png")
        {
            return "image/png";
        }

        if (normalized is "image/webp")
        {
            return "image/webp";
        }

        return extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => null,
        };
    }

    private static string GetAvatarMimeType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "image/jpeg",
        };

    private static float ParseAvatarZoom(string? value)
    {
        if (!float.TryParse(
                value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var zoom))
        {
            return 1f;
        }

        return Math.Clamp(zoom, 1f, 3f);
    }

    private static async Task SaveAvatarImageAsync(
        Stream upload,
        string targetPath,
        string extension,
        float zoom,
        CancellationToken ct)
    {
        using var input = new MemoryStream();
        await upload.CopyToAsync(input, ct);
        input.Position = 0;

        using var bitmap = SKBitmap.Decode(input);
        if (bitmap is null)
        {
            throw new ArgumentException("Avatar image could not be decoded.");
        }

        var cropSize = Math.Max(1, (int)MathF.Round(Math.Min(bitmap.Width, bitmap.Height) / zoom));
        var cropLeft = Math.Max(0, (bitmap.Width - cropSize) / 2);
        var cropTop = Math.Max(0, (bitmap.Height - cropSize) / 2);
        var source = new SKRectI(cropLeft, cropTop, cropLeft + cropSize, cropTop + cropSize);
        var destination = new SKRect(0, 0, 512, 512);

        using var surface = SKSurface.Create(new SKImageInfo(512, 512, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { IsAntialias = true };
        using var imageSource = SKImage.FromBitmap(bitmap);
        surface.Canvas.DrawImage(
            imageSource,
            new SKRect(source.Left, source.Top, source.Right, source.Bottom),
            destination,
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear),
            paint);

        using var image = surface.Snapshot();
        using var data = image.Encode(GetAvatarEncodeFormat(extension), 92);
        await using var output = File.Create(targetPath);
        data.SaveTo(output);
    }

    internal static async Task<IResult> UpdateExperienceAsync(Guid id, UpdateProfileExperienceRequest request,
        IProfileService profiles, CancellationToken ct)
    {
        var name = request.DisplayName?.Trim();
        var color = request.AvatarColor?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 50 || color is null || color.Length != 7
            || color[0] != '#' || color[1..].Any(character => !Uri.IsHexDigit(character)))
        {
            return ApiErrors.BadRequest("Use a name of 1–50 characters and an avatar color in #RRGGBB format.");
        }

        if (request.NavigationConfig is { } navigation)
        {
            if (System.Text.Encoding.UTF8.GetByteCount(navigation) > 65_536)
            {
                return ApiErrors.BadRequest("Navigation preferences are too large.");
            }

            try
            {
                using var parsed = System.Text.Json.JsonDocument.Parse(navigation);
                if (parsed.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                {
                    return ApiErrors.BadRequest("Navigation preferences must be a JSON object.");
                }
            }
            catch (System.Text.Json.JsonException) { return ApiErrors.BadRequest("Navigation preferences must be valid JSON."); }
        }
        var profile = await profiles.GetProfileAsync(id, ct);
        if (profile is null)
        {
            return ApiErrors.NotFound("Profile not found.");
        }

        profile.DisplayName = name;
        profile.AvatarColor = color.ToUpperInvariant();
        profile.NavigationConfig = request.NavigationConfig;
        return await profiles.UpdateProfileAsync(profile, ct)
            ? Results.NoContent() : ApiErrors.Conflict("Profile preferences could not be saved. Refresh and try again.");
    }

    private static SKEncodedImageFormat GetAvatarEncodeFormat(string extension) =>
        extension.ToLowerInvariant() switch
        {
            ".png" => SKEncodedImageFormat.Png,
            ".webp" => SKEncodedImageFormat.Webp,
            _ => SKEncodedImageFormat.Jpeg,
        };

}
