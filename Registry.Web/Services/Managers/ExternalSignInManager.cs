using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;

namespace Registry.Web.Services.Managers
{
    public class ExternalSignInManager : SignInManager<User>
    {
        private readonly AppSettings _settings;

        public ExternalSignInManager(UserManager<User> userManager,
            IHttpContextAccessor contextAccessor,
            IUserClaimsPrincipalFactory<User> claimsFactory,
            IOptions<IdentityOptions> optionsAccessor,
            ILogger<SignInManager<User>> logger,
            IAuthenticationSchemeProvider schemes,
            IUserConfirmation<User> confirmation,
            IOptions<AppSettings> settings) :
            base(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
        {
            _settings = settings.Value;
        }

        public async Task<SignInResult> CheckTokenSignInAsync(string token)
        {

            var client = new HttpClient();

            try
            {
                var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("token", token),
                });

                var res = await client.PostAsync(_settings.ExternalAuthUrl, content);

                if (!res.IsSuccessStatusCode)
                    return SignInResult.Failed;

                var result = await res.Content.ReadAsStringAsync();

                var obj = JsonConvert.DeserializeObject<Dictionary<string, object>>(result);

                //if (obj != null)
                //{
                //    user.Metadata = obj;
                //}

                Logger.LogInformation(result);

                return SignInResult.Success;
            }
            catch (WebException ex)
            {
                Logger.LogError(ex, "Exception in calling CanSignInAsync");
                return SignInResult.NotAllowed;
            }
        }


        // This is basically a stub
        public override async Task<SignInResult> CheckPasswordSignInAsync(User user, string password, bool lockoutOnFailure)
        {

            var client = new HttpClient();

            try
            {
                var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("username", user.UserName),
                    new KeyValuePair<string, string>("password", password)
                });

                var res = await client.PostAsync(_settings.ExternalAuthUrl, content);

                if (!res.IsSuccessStatusCode)
                    return SignInResult.Failed;

                var result = await res.Content.ReadAsStringAsync();

                var obj = JsonConvert.DeserializeObject<Dictionary<string, object>>(result);

                //if (obj != null)
                //{
                //    user.Metadata = obj;
                //}

                Logger.LogInformation(result);

                return SignInResult.Success;
            }
            catch (WebException ex)
            {
                Logger.LogError(ex, "Exception in calling CanSignInAsync");
                return SignInResult.NotAllowed;
            }
        }

    }
}
