using System.Buffers.Binary;
using System.Security.Cryptography;
using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Web.Services.Integration;

public enum ViewMediaResourceKind : byte
{
    Thumbnail = 1,
    Content = 2,
    Preview = 3,
}

public enum ViewMediaResourceRole : byte
{
    Primary = 1,
    Original = 2,
    LivePhotoVideo = 3,
    Raw = 4,
    Jpeg = 5,
    Sidecar = 6,
    AudioCompanion = 7,
}

public sealed record ViewMediaGrant(
    Guid ProfileId,
    Guid LibraryId,
    Guid AssetId,
    ViewMediaResourceKind ResourceKind,
    ViewMediaResourceRole ResourceRole,
    ViewScopeKind ScopeKind,
    Guid? ScopeProfileId,
    DateTimeOffset ExpiresAt);

public sealed record ViewMediaGrantToken(string Value, DateTimeOffset ExpiresAt);

/// <summary>
/// Mints and validates opaque, short-lived bearer grants for one exact View media resource.
/// </summary>
public sealed class ViewMediaGrantService
{
    private const byte Version = 2;
    private const int PayloadLength = 76;
    private const int SignatureLength = 32;

    private readonly byte[] _key;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _lifetime;

    public ViewMediaGrantService(
        byte[] key,
        TimeSpan lifetime,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length < 32)
        {
            throw new ArgumentException("View media grant keys must contain at least 32 bytes.", nameof(key));
        }

        _key = [.. key];
        _lifetime = lifetime;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ViewMediaGrantToken Create(
        Guid profileId,
        Guid libraryId,
        Guid assetId,
        ViewMediaResourceKind resourceKind,
        ViewMediaResourceRole resourceRole = ViewMediaResourceRole.Primary,
        ViewScopeKind scopeKind = ViewScopeKind.Mine,
        Guid? scopeProfileId = null)
    {
        if (!Enum.IsDefined(scopeKind)
            || (scopeKind.CarriesProfileId() && !scopeProfileId.HasValue)
            || (!scopeKind.CarriesProfileId() && scopeProfileId.HasValue))
        {
            throw new ArgumentException("The View scope and scope profile must describe one valid authorized scope.", nameof(scopeKind));
        }

        var expiresAt = _timeProvider.GetUtcNow().Add(_lifetime);
        Span<byte> payload = stackalloc byte[PayloadLength];
        payload[0] = Version;
        profileId.TryWriteBytes(payload[1..17], bigEndian: true, out _);
        libraryId.TryWriteBytes(payload[17..33], bigEndian: true, out _);
        assetId.TryWriteBytes(payload[33..49], bigEndian: true, out _);
        payload[49] = (byte)resourceKind;
        payload[50] = (byte)resourceRole;
        payload[51] = (byte)scopeKind;
        (scopeProfileId ?? Guid.Empty).TryWriteBytes(payload[52..68], bigEndian: true, out _);
        BinaryPrimitives.WriteInt64BigEndian(payload[68..76], expiresAt.ToUnixTimeSeconds());

        var signature = HMACSHA256.HashData(_key, payload);
        return new ViewMediaGrantToken(
            $"{Base64UrlEncode(payload)}.{Base64UrlEncode(signature)}",
            expiresAt);
    }

    public bool TryValidate(string? token, out ViewMediaGrant? grant)
    {
        grant = null;
        if (string.IsNullOrWhiteSpace(token) || token.Length > 256)
        {
            return false;
        }

        var separator = token.IndexOf('.');
        if (separator <= 0 || separator != token.LastIndexOf('.'))
        {
            return false;
        }

        if (!TryBase64UrlDecode(token[..separator], out var payload)
            || !TryBase64UrlDecode(token[(separator + 1)..], out var suppliedSignature)
            || payload.Length != PayloadLength)
        {
            return false;
        }

        var expectedSignature = HMACSHA256.HashData(_key, payload);
        Span<byte> normalizedSignature = stackalloc byte[SignatureLength];
        suppliedSignature.AsSpan(0, Math.Min(suppliedSignature.Length, SignatureLength))
            .CopyTo(normalizedSignature);
        var signatureValid = suppliedSignature.Length == SignatureLength
            & CryptographicOperations.FixedTimeEquals(expectedSignature, normalizedSignature);
        if (!signatureValid || payload[0] != Version)
        {
            return false;
        }

        var resourceKind = (ViewMediaResourceKind)payload[49];
        var resourceRole = (ViewMediaResourceRole)payload[50];
        var scopeKind = (ViewScopeKind)payload[51];
        var encodedScopeProfileId = new Guid(payload.AsSpan(52, 16), bigEndian: true);
        Guid? scopeProfileId = encodedScopeProfileId == Guid.Empty ? null : encodedScopeProfileId;
        if (!Enum.IsDefined(resourceKind)
            || !Enum.IsDefined(resourceRole)
            || !Enum.IsDefined(scopeKind)
            || (scopeKind.CarriesProfileId() && !scopeProfileId.HasValue)
            || (!scopeKind.CarriesProfileId() && scopeProfileId.HasValue))
        {
            return false;
        }

        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(
            BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(68, 8)));
        if (expiresAt <= _timeProvider.GetUtcNow())
        {
            return false;
        }

        grant = new ViewMediaGrant(
            new Guid(payload.AsSpan(1, 16), bigEndian: true),
            new Guid(payload.AsSpan(17, 16), bigEndian: true),
            new Guid(payload.AsSpan(33, 16), bigEndian: true),
            resourceKind,
            resourceRole,
            scopeKind,
            scopeProfileId,
            expiresAt);
        return true;
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static bool TryBase64UrlDecode(string value, out byte[] bytes)
    {
        bytes = [];
        if (value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            return false;
        }

        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => string.Empty };
        try
        {
            bytes = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
