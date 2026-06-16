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
    /// Every implementation must declare this explicitly: <see cref="LocalLoginManager"/> returns
    /// <see cref="AuthProviderCapabilities.Local"/>, while external providers (LDAP/Remote) return
    /// <see cref="AuthProviderCapabilities.External"/>. No default is provided so that callers and
    /// test doubles are forced to choose an explicit capability set.
    /// </summary>
    AuthProviderCapabilities Capabilities { get; }
}