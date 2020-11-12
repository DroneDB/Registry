using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Registry.Web.Models
{
    
    public class AuthenticateResponse
    {
        public string Id { get; }
        public string Token { get; }

        [JsonConverter(typeof(UnixDateTimeConverter))]
        public DateTime Expires { get; }
        
        public AuthenticateResponse(User user, string token, DateTime expires)
        {
            Id = user.Id;
            Token = token;
            Expires = expires;
        }
    }
}
