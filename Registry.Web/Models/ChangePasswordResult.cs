namespace Registry.Web.Models;

/// <summary>The credentials after a password change (built in code only, never deserialized).</summary>
public class ChangePasswordResult
{
    public required string UserName { get; set; }
    public required string Password { get; set; }
}