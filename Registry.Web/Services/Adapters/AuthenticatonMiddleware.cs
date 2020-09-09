using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json;
using System.Text;

namespace Registry.Web.Services.Adapters
{
    public class AuthenticationMiddleware
    {
        private static void writeErrorResponse(HttpContext Context, string error)
        {
            Context.Response.ContentType = "application/json";
            string json = JsonConvert.SerializeObject(new{
                error = error
            });
            byte[] data = Encoding.UTF8.GetBytes(json);
            Context.Response.Body.WriteAsync(data, 0, data.Length);
        }

        private readonly RequestDelegate _request;

        public AuthenticationMiddleware(RequestDelegate RequestDelegate)
        {
            if (RequestDelegate == null)
            {
                throw new ArgumentNullException(nameof(RequestDelegate)
                    , nameof(RequestDelegate) + " is required");
            }

            _request = RequestDelegate;
        }

        public async Task InvokeAsync(HttpContext Context)
        {
            if (Context == null)
            {
                throw new ArgumentNullException(nameof(Context)
                    , nameof(Context) + " is required");
            }

            await _request(Context);

            if(Context.Response.StatusCode == 401)
            {
                writeErrorResponse(Context, "Unauthorized");
            }
        }
    }
}