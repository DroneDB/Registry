namespace Registry.Web.Models;

public class ChangeUserPasswordResult
{
    public string UserName { get; set; }
    public string Password { get; set; }
    public string Token { get; set; }
}