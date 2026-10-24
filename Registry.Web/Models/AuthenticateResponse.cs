using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json.Serialization;
using Registry.Ports.Json;
using Registry.Web.Identity.Models;

namespace Registry.Web.Models;

public class AuthenticateResponse
{
    public string Username { get; }
    public string Token { get; }

    [JsonConverter(typeof(UnixSecondsConverter))]
    public DateTime Expires { get; }
        
    public AuthenticateResponse(User user, string token, DateTime expires)
    {
        Username = user.UserName;
        Token = token;
        Expires = expires;
    }
}