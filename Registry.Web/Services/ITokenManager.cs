using System;
using System.Collections.Generic;
using Microsoft.Extensions.Caching.Distributed;

namespace Registry.Web.Services
{
    /// <summary>
    /// Manages the JWT tokens
    /// </summary>
    public interface ITokenManager
    {
        bool IsCurrentActiveToken();
        bool IsActive(string token);

    }
}
