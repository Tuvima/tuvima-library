using MediaEngine.Domain.Authorization;

namespace MediaEngine.Contracts.Authentication;

/// <summary>
/// Wire vocabulary for <c>original_client_ingress</c> and the <see cref="ValidateHeader"/> request header.
/// Unknown or missing values are <see cref="Remote"/>.
/// </summary>
public static class ClientIngressValues
{
    public const string ThisComputer = ClientIngress.ThisComputer;
    public const string HomeNetwork = ClientIngress.HomeNetwork;
    public const string Remote = ClientIngress.Remote;

    /// <summary>Sent by the Dashboard on <c>POST /auth/session/validate</c>; only honoured with the service credential.</summary>
    public const string ValidateHeader = "X-Tuvima-Client-Ingress";

    /// <summary>401 reason returned when a home session is used from outside.</summary>
    public const string SignInAgainHere = "sign_in_again_here";

    public static string Parse(string? value) => ClientIngress.Parse(value);
}
