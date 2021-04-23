using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Registry.Common;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Serilog.Core;

namespace Registry.Web.Services.Managers
{
    public class RemoteLoginManager : ILoginManager
    {
        private readonly ILogger<ILoginManager> _logger;
        private readonly AppSettings _settings;

        public RemoteLoginManager(ILogger<ILoginManager> logger,
            IOptions<AppSettings> settings)
        {
            _logger = logger;
            _settings = settings.Value;
        }

        public async Task<LoginResult> CheckAccess(string token)
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
                    return new LoginResult
                    {
                        Success = false
                    };

                var result = await res.Content.ReadAsStringAsync();

                var obj = JsonConvert.DeserializeObject<Dictionary<string, object>>(result);

                _logger.LogInformation(result);

                return new LoginResult
                {
                    Success = true,
                    UserName = obj?.SafeGetValue("username") as string,
                    Metadata = obj
                };
            }
            catch (WebException ex)
            {
                _logger.LogError(ex, "Exception in calling CanSignInAsync");
                return new LoginResult
                {
                    Success = false,
                };
            }
        }


        // This is basically a stub
        public async Task<LoginResult> CheckAccess(string userName, string password)
        {

            var client = new HttpClient();

            try
            {
                var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("username", userName),
                    new KeyValuePair<string, string>("password", password)
                });

                var res = await client.PostAsync(_settings.ExternalAuthUrl, content);

                if (!res.IsSuccessStatusCode)
                    return new LoginResult
                    {
                        UserName = userName,
                        Success = false
                    };

                var result = await res.Content.ReadAsStringAsync();

                var obj = JsonConvert.DeserializeObject<Dictionary<string, object>>(result);
               
                _logger.LogInformation(result);

                return new LoginResult
                {
                    Success = true,
                    UserName = obj?.SafeGetValue("username") as string ?? userName,
                    Metadata = obj
                };
            }
            catch (WebException ex)
            {
                _logger.LogError(ex, "Exception in calling CanSignInAsync");
                return new LoginResult
                {
                    Success = false,
                    UserName = userName
                };
            }
        }

    }
}
