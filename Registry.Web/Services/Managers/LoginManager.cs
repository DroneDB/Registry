using System;
using System.Collections.Generic;
using System.Linq;
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
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Serilog.Core;

namespace Registry.Web.Services.Managers
{
    public class LocalLoginManager : ILoginManager
    {
        private readonly UserManager<User> _userManager;
        private readonly ILogger<ILoginManager> _logger;
        private readonly AppSettings _settings;

        public LocalLoginManager(UserManager<User> userManager,
            ILogger<ILoginManager> logger,
            IOptions<AppSettings> settings)
        {
            _userManager = userManager;
            _logger = logger;
            _settings = settings.Value;
        }

        public async Task<LoginResult> CheckTokenSignInAsync(string token)
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

                _logger.LogInformation(result);

                return SignInResult.Success;
            }
            catch (WebException ex)
            {
                _logger.LogError(ex, "Exception in calling CanSignInAsync");
                return SignInResult.NotAllowed;
            }
        }


        // This is basically a stub
        public async Task<SignInResult> CheckPasswordSignInAsync(User user, string password, bool lockoutOnFailure)
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

                if (obj != null)
                {
                    user.Metadata = obj;
                }

                _logger.LogInformation(result);

                return SignInResult.Success;
            }
            catch (WebException ex)
            {
                _logger.LogError(ex, "Exception in calling CanSignInAsync");
                return SignInResult.NotAllowed;
            }
        }
    }
}
