using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Registry.Web.Models;

namespace Registry.Web.Services
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
            var authorizationHeader = _httpContextAccessor
                .HttpContext.Request.Headers["authorization"];

            return authorizationHeader == StringValues.Empty
                ? string.Empty
                : authorizationHeader.Single().Split(" ").Last();
        }

    }
}