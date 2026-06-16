using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports;

public interface ILoginManager
{
    Task<LoginResultDto> CheckAccess(string userName, string password);
    Task<LoginResultDto> CheckAccess(string token);

    /// <summary>
    /// Describes what this provider allows to manage locally.
    /// Defaults to <see cref="AuthProviderCapabilities.External"/> so that any provider
    /// that forgets to override it "fails closed" (disables local management rather than exposing it).
    /// <see cref="LocalLoginManager"/> overrides this to <see cref="AuthProviderCapabilities.Local"/>.
    /// </summary>
    AuthProviderCapabilities Capabilities => AuthProviderCapabilities.External;
}