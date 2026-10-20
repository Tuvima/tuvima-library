using MediaEngine.AI.Configuration;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using Microsoft.Extensions.Logging;

namespace MediaEngine.AI.Infrastructure;

/// <summary>Tracks verified model artifacts and the roles that share each artifact.</summary>
public sealed class ModelInventory
{
    private static readonly StringComparer ArtifactComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly AiRuntimeSettingsSnapshot _settings;
    private readonly AiSettings _liveSettings;
    private readonly ILogger<ModelInventory> _logger;
    private readonly Dictionary<AiModelRole, AiModelState> _states = [];
    private readonly object _lock = new();

    public ModelInventory(AiSettings settings, ILogger<ModelInventory> logger)
    {
        _settings = AiRuntimeSettingsSnapshot.Create(settings);
        _liveSettings = settings;
        _logger = logger;
        Refresh();
    }

    /// <summary>Scans each unique artifact once and applies its state to every sharing role.</summary>
    public void Refresh()
    {
        lock (_lock)
        {
            var refreshed = new HashSet<string>(ArtifactComparer);
            foreach (var role in Enum.GetValues<AiModelRole>())
            {
                var artifact = GetArtifactKey(role);
                if (refreshed.Add(artifact))
                {
                    RefreshArtifactCore(role);
                }
            }
        }
    }

    /// <summary>Refreshes all roles backed by the same resolved model file.</summary>
    public void RefreshArtifact(AiModelRole role)
    {
        lock (_lock)
        {
            RefreshArtifactCore(role);
        }
    }

    public AiModelState GetState(AiModelRole role)
    {
        lock (_lock)
        {
            return _states.GetValueOrDefault(role, AiModelState.NotDownloaded);
        }
    }

    public void SetState(AiModelRole role, AiModelState state)
    {
        lock (_lock)
        {
            _states[role] = state;
        }
    }

    /// <summary>Sets the state for every role backed by the same model file.</summary>
    public void SetArtifactState(AiModelRole role, AiModelState state)
    {
        lock (_lock)
        {
            foreach (var sharedRole in GetRolesSharingArtifactCore(role))
            {
                _states[sharedRole] = state;
            }
        }
    }

    public string GetModelPath(AiModelRole role)
    {
        var definition = _settings.GetModel(role);
        var subdirectory = role == AiModelRole.Audio ? "whisper" : "llama";
        var roleDirectory = Path.GetFullPath(Path.Combine(_settings.ModelsDirectory, subdirectory));
        var candidate = Path.GetFullPath(Path.Combine(roleDirectory, definition.File));
        var requiredPrefix = roleDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? roleDirectory
            : roleDirectory + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!candidate.StartsWith(requiredPrefix, comparison))
        {
            throw new InvalidOperationException($"Model file for {role} resolves outside its managed directory.");
        }

        return candidate;
    }

    public string GetArtifactKey(AiModelRole role) => GetModelPath(role);

    public IReadOnlyList<AiModelRole> GetRolesSharingArtifact(AiModelRole role)
    {
        lock (_lock)
        {
            return GetRolesSharingArtifactCore(role);
        }
    }

    public AiModelRuntimeDefinition GetDefinition(AiModelRole role) => _settings.GetModel(role);

    public bool AreAllReady()
    {
        lock (_lock)
        {
            var textReady = _states.GetValueOrDefault(AiModelRole.TextQuality)
                is AiModelState.Ready or AiModelState.Loaded;
            var audioReady = !_liveSettings.AudioPackEnabled || _states.GetValueOrDefault(AiModelRole.Audio)
                is AiModelState.Ready or AiModelState.Loaded;
            return textReady && audioReady;
        }
    }

    /// <summary>
    /// True when the text model file is present and verified. The optional audio pack is deliberately not part of this:
    /// text features work whether or not it is downloaded.
    /// </summary>
    public bool IsTextModelReady()
    {
        lock (_lock)
        {
            return _states.GetValueOrDefault(AiModelRole.TextQuality)
                is AiModelState.Ready or AiModelState.Loaded;
        }
    }

    public IReadOnlyList<AiModelStatus> GetAllStatuses()
    {
        lock (_lock)
        {
            var roles = _liveSettings.AudioPackEnabled
                ? new[] { AiModelRole.TextQuality, AiModelRole.Audio }
                : new[] { AiModelRole.TextQuality };
            return roles.Select(role =>
            {
                var definition = _settings.GetModel(role);
                return new AiModelStatus
                {
                    Role = role,
                    ModelType = role == AiModelRole.Audio ? AiModelType.Audio : AiModelType.Text,
                    State = _states.GetValueOrDefault(role, AiModelState.NotDownloaded),
                    ModelFile = definition.File,
                    SizeMB = definition.SizeMB,
                };
            }).ToList();
        }
    }

    private void RefreshArtifactCore(AiModelRole role)
    {
        var sharedRoles = GetRolesSharingArtifactCore(role);
        var modelPath = GetModelPath(role);
        AiModelState artifactState;
        if (!File.Exists(modelPath))
        {
            artifactState = AiModelState.NotDownloaded;
        }
        else if (ValidateExistingArtifact(modelPath, sharedRoles))
        {
            artifactState = AiModelState.Ready;
        }
        else
        {
            artifactState = AiModelState.Error;
        }

        foreach (var sharedRole in sharedRoles)
        {
            if (_states.GetValueOrDefault(sharedRole) == AiModelState.Loaded
                && artifactState == AiModelState.Ready)
            {
                continue;
            }

            _states[sharedRole] = artifactState;
            _logger.LogInformation(
                "Model artifact for {Role} at {Path} is {State}",
                sharedRole,
                modelPath,
                artifactState);
        }
    }

    private AiModelRole[] GetRolesSharingArtifactCore(AiModelRole role)
    {
        var artifact = GetArtifactKey(role);
        return Enum.GetValues<AiModelRole>()
            .Where(candidate => ArtifactComparer.Equals(GetArtifactKey(candidate), artifact))
            .ToArray();
    }

    private bool ValidateExistingArtifact(string modelPath, IReadOnlyList<AiModelRole> roles)
    {
        try
        {
            var file = new FileInfo(modelPath);
            if (file.Length <= 0)
            {
                _logger.LogWarning("Shared model artifact is empty: {Path}", modelPath);
                return false;
            }

            var checksums = roles
                .Select(role => _settings.GetModel(role).Sha256)
                .Where(checksum => !string.IsNullOrWhiteSpace(checksum))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (checksums.Length == 0)
            {
                return true;
            }

            if (checksums.Length > 1)
            {
                _logger.LogError("Roles sharing {Path} specify conflicting checksums", modelPath);
                return false;
            }

            using var stream = File.OpenRead(modelPath);
            var actual = Hashing.Sha256Hex(stream);
            if (actual.Equals(checksums[0], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            _logger.LogError(
                "Model checksum validation failed for {Path}: expected {Expected}, got {Actual}",
                modelPath,
                checksums[0],
                actual);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not validate shared model artifact at {Path}", modelPath);
            return false;
        }
    }
}
