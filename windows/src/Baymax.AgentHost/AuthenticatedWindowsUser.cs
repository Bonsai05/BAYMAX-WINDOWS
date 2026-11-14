using System.Security.Claims;

namespace Baymax.AgentHost;

public static class AuthenticatedWindowsUser
{
    private static readonly string HostAccount = $"{Environment.UserDomainName}\\{Environment.UserName}";

    public static string GetName(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var identity = user.Identity;
        if (identity is not { IsAuthenticated: true }
            || string.IsNullOrWhiteSpace(identity.Name)
            || !string.Equals(identity.Name, HostAccount, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                "File operations require Windows authentication as the same account running Baymax.");
        }

        return identity.Name;
    }
}
