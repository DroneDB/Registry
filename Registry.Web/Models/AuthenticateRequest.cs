#nullable disable
namespace Registry.Web.Models;

public class AuthenticateRequest
{
    public string Username { get; set; }

    public string Password { get; set; }

    public string Token { get; set; }
}