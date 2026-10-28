using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;

namespace Registry.Web.Services.Users;

/// <summary>Issues the JWT of a signed-in user.</summary>
public sealed class JwtTokenIssuer
{
    private readonly AuthTokenOptions _tokens;
    private readonly UserManager<User> _userManager;
    private readonly TimeProvider _time;

    public JwtTokenIssuer(IOptions<AuthTokenOptions> tokens, UserManager<User> userManager, TimeProvider time)
    {
        _tokens = tokens.Value;
        _userManager = userManager;
        _time = time;
    }

    /// <summary>
    /// A token for <paramref name="user"/>: their id as the name claim, whether they are an administrator, signed with
    /// <see cref="AuthTokenOptions.Secret"/>, valid <see cref="AuthTokenOptions.TokenExpirationInDays"/> days.
    /// </summary>
    public async Task<JwtDescriptor> IssueAsync(User user)
    {
        ArgumentNullException.ThrowIfNull(user);

        var tokenHandler = new JwtSecurityTokenHandler();
        // The options validation refuses to start without a secret (the first start generates it)
        var key = Encoding.ASCII.GetBytes(_tokens.Secret!);
        var expiresOn = _time.GetUtcNow().UtcDateTime.AddDays(_tokens.TokenExpirationInDays);
        var isAdmin = await _userManager.IsInRoleAsync(user, ApplicationDbContext.AdminRoleName);

        var token = tokenHandler.CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([
                new Claim(ClaimTypes.Name, user.Id),
                new Claim(ApplicationDbContext.AdminRoleName.ToLowerInvariant(), isAdmin.ToString(),
                    ClaimValueTypes.Boolean)
            ]),
            Expires = expiresOn,
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key),
                SecurityAlgorithms.HmacSha256Signature)
        });

        return new JwtDescriptor
        {
            Token = tokenHandler.WriteToken(token),
            ExpiresOn = expiresOn
        };
    }
}
