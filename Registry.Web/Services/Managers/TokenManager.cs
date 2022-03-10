using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Managers
{
    public class TokenManager : ITokenManager
    {
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly AppSettings _appSettings;

        public TokenManager(IHttpContextAccessor httpContextAccessor,
            IOptions<AppSettings> settings
        )
        {
            _httpContextAccessor = httpContextAccessor;
            _appSettings = settings.Value;
        }

        public bool IsCurrentActiveToken() {
            return IsActive(GetCurrent());
        }
 
        public bool IsActive(string token)
        {
            return _appSettings.RevokedTokens?.FirstOrDefault(item => item == token) == null;
        }

        private string GetCurrent()
        {
            var request = _httpContextAccessor?.HttpContext?.Request;

            if (request == null)
                return null;

            var authorizationHeader = request.Headers["authorization"];

            return 
                authorizationHeader == StringValues.Empty ? 
                    request.Cookies[_appSettings.AuthCookieName] : 
                    authorizationHeader.SingleOrDefault(h => h.StartsWith("Bearer", System.StringComparison.OrdinalIgnoreCase), "").Split(" ").Last();
        }

    }
}