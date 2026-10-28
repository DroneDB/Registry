#nullable disable
namespace Registry.Web.Services.Ports;

/// <summary>
/// Manages the JWT tokens
/// </summary>
public interface ITokenManager
{
    bool IsCurrentActiveToken();
}