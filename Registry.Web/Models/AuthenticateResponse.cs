#nullable disable
using System;
using System.Text.Json.Serialization;
using Registry.Ports.Json;

namespace Registry.Web.Models;

public class AuthenticateResponse
{
    public string Username { get; }
    public string Token { get; }

    [JsonConverter(typeof(UnixSecondsConverter))]
    public DateTime Expires { get; }
        
    public AuthenticateResponse(string username, string token, DateTime expires)
    {
        Username = username;
        Token = token;
        Expires = expires;
    }
}