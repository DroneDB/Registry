using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Registry.Web.Identity.Models;

namespace Registry.Web.Models
{
    
    public class AuthenticateResponse
    {
        public string Username { get; }
        public string Token { get; }

        [JsonConverter(typeof(UnixDateTimeConverter))]
        public DateTime Expires { get; }
        
        public AuthenticateResponse(User user, string token, DateTime expires)
        {
            Username = user.UserName;
            Token = token;
            Expires = expires;
        }
    }
}
