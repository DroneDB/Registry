#nullable enable
namespace Registry.Web.Models.Configuration;

/// <summary>Tokens the Registry issues and accepts (keys of the <c>AppSettings</c> section).</summary>
public sealed class AuthTokenOptions
{
    /// <summary>
    /// Secret that signs the JWT tokens. Generated on the first start when empty and stored in the
    /// storage folder's <c>appsettings.json</c>.
    /// </summary>
    public string? Secret { get; set; }

    /// <summary>JWT token expiration in days (at least 1).</summary>
    public int TokenExpirationInDays { get; set; }

    /// <summary>JWT tokens that are no longer accepted.</summary>
    public string[]? RevokedTokens { get; set; }

    /// <summary>Name of the cookie that carries the JWT token.</summary>
    public string? AuthCookieName { get; set; }

    /// <summary>Token that lets a monitor call the health checks (<c>?token=</c>); null disables it.</summary>
    public string? MonitorToken { get; set; }
}
