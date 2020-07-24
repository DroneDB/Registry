using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Registry.Web.Models;

namespace Registry.Web.Services
{

    public interface ITokenManager
    {
        bool IsCurrentActiveToken();
        bool IsActive(string token);

    }
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

    public class TokenManagerMiddleware : IMiddleware
    {
        private readonly ITokenManager _tokenManager;

        public TokenManagerMiddleware(ITokenManager tokenManager)
        {
            _tokenManager = tokenManager;
        }

        public async Task InvokeAsync(HttpContext context, RequestDelegate next)
        {
            if (_tokenManager.IsCurrentActiveToken())
            {
                await next(context);

                return;
            }
            context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
        }
    }


}
