using System.Collections.Generic;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using Registry.Web.Models.Configuration;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Registry.Web.Filters
{
    public class BasePathDocumentFilter : IDocumentFilter
    {
        private readonly AppSettings _settings;

        public BasePathDocumentFilter(IOptions<AppSettings> settings)
        {
            _settings = settings.Value;
        }

        public void Apply(OpenApiDocument swaggerDoc, DocumentFilterContext context)
        {
            if (!string.IsNullOrWhiteSpace(_settings.ExternalUrlOverride))
                swaggerDoc.Servers = new List<OpenApiServer>
                {
                    new()
                    {
                        Url = _settings.ExternalUrlOverride
                    }
                };
        }
    }
}
