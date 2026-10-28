#nullable disable
using System;
using System.Net.Http;

namespace Registry.Web.Test;

/// <summary>
/// Simple HttpClientFactory for testing that creates real HttpClient instances
/// </summary>
internal sealed class TestHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name)
    {
        return new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(30)
        };
    }
}
