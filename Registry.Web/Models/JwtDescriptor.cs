using System;

namespace Registry.Web.Models;

public class JwtDescriptor
{
    public string Token { get; set; }
    public DateTime ExpiresOn { get; set; }
}