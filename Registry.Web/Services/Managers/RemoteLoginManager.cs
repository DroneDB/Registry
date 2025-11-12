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

namespace Registry.Web.Services.Managers;

public class RemoteLoginManager : ILoginManager
{
    private readonly ILogger<ILoginManager> _logger;
    private readonly AppSettings _settings;
    private readonly IHttpClientFactory _httpClientFactory;

    public RemoteLoginManager(ILogger<RemoteLoginManager> logger,
        IOptions<AppSettings> settings,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _settings = settings.Value;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<LoginResultDto> CheckAccess(string token)
    {

        var client = _httpClientFactory.CreateClient();

        try
        {
            var content = new FormUrlEncodedContent([
                new KeyValuePair<string, string>("token", token)
            ]);

            var res = await client.PostAsync(_settings.ExternalAuthUrl, content);

            if (!res.IsSuccessStatusCode)
                return new LoginResultDto
                {
                    Success = false
                };

            var result = await res.Content.ReadAsStringAsync();

            var obj = JsonConvert.DeserializeObject<Dictionary<string, object>>(result);

            _logger.LogInformation("CheckAccess (token): {Result}", result);

            return new LoginResultDto
            {
                Success = true,
                UserName = obj?.SafeGetValue("username") as string,
                Metadata = obj
            };
        }
        catch (WebException ex)
        {
            _logger.LogError(ex, "Exception in calling CheckAccess (token)");
            return new LoginResultDto
            {
                Success = false,
            };
        }
    }

    public async Task<LoginResultDto> CheckAccess(string userName, string password)
    {

        var client = _httpClientFactory.CreateClient();

        try
        {
            var content = new FormUrlEncodedContent([
                new KeyValuePair<string, string>("username", userName),
                new KeyValuePair<string, string>("password", password)
            ]);

            client.DefaultRequestHeaders.Add("Accept", "application/json");

            var res = await client.PostAsync(_settings.ExternalAuthUrl, content);

            if (!res.IsSuccessStatusCode)
            {
                var c = await res.Content.ReadAsStringAsync();
                _logger.LogInformation("Error while calling external auth service on url {Url}, result code: {ResultCode}, content {Result}",
                    _settings.ExternalAuthUrl, res.StatusCode, c);

                return new LoginResultDto
                {
                    UserName = userName,
                    Success = false
                };
            }

            var result = await res.Content.ReadAsStringAsync();
            _logger.LogInformation("CheckAccess for user {User}: {Result}", userName, result);

            var obj = JsonConvert.DeserializeObject<Dictionary<string, object>>(result);

            return new LoginResultDto
            {
                Success = true,
                UserName = obj?.SafeGetValue("username") as string ?? userName,
                Metadata = obj
            };
        }
        catch (WebException ex)
        {
            _logger.LogError(ex, "Exception in calling CheckAccess for user {User}", userName);
            return new LoginResultDto
            {
                Success = false,
                UserName = userName
            };
        }
    }

}