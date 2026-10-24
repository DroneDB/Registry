#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using System.Text.Json;
using System.Text.Json.Nodes;
using Registry.Web.Filters;
using Registry.Web.Models.Configuration;
using Registry.Web.Utilities;

namespace Registry.Web.Hosting;

/// <summary>The HTTP API of the web host: MVC, the error model, OpenAPI, caching, compression, rate limits and the HTTP edge.</summary>
public static class RegistryApiServiceCollectionExtensions
{
    /// <summary>MVC with the RQ-05 error model, the OpenAPI document, response caching and compression, rate limiting.</summary>
    public static IServiceCollection AddRegistryApi(this IServiceCollection services)
    {
        services.AddControllers(o =>
        {
            o.Filters.Add<ApiExceptionFilter>();
            // Dataset paths are checked by libddb's rule while the request is bound (RQ-11)
            o.ModelBinderProviders.Insert(0, new DatasetPathModelBinderProvider());
        });

        // {orgSlug:slug} and {dsSlug:slug}: the slug rules decide what routes, as they decide what is created
        services.Configure<RouteOptions>(o => o.ConstraintMap[RoutesHelper.SlugConstraint] = typeof(SlugRouteConstraint));

        // One error model (RQ-05, D1): RFC 9457 ProblemDetails written by ApiErrorResponder from the MVC
        // filter, the model-state factory below and the pipeline-wide exception handler
        // Every ProblemDetails carries noRetry, also the framework's own (e.g. a bare NotFound()); the
        // responder sets the real value of a classified error after this default
        services.AddProblemDetails(o => o.CustomizeProblemDetails = context =>
            context.ProblemDetails.Extensions.TryAdd(ApiErrorResponder.NoRetryMember, false));
        services.AddSingleton<ApiErrorResponder>();
        services.AddExceptionHandler<ApiExceptionHandler>();

        // Model validation errors: ValidationProblemDetails whose detail carries the field messages. The
        // responder is resolved per request: it needs ProblemDetailsFactory, which reads these options
        services.Configure<ApiBehaviorOptions>(o =>
        {
            o.InvalidModelStateResponseFactory = actionContext =>
                ApiErrorResponder.ToActionResult(actionContext.HttpContext.RequestServices
                    .GetRequiredService<ApiErrorResponder>()
                    .CreateValidation(actionContext.HttpContext, actionContext.ModelState));
        });

        services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new OpenApiInfo
            {
                Version = "v1",
                Title = "Registry API",
                Description = "API to manage DroneDB Registry",
                Contact = new OpenApiContact
                {
                    Name = "DroneDB Support",
                    Email = "support@dronedb.app",
                    Url = new Uri("https://dronedb.app/"),
                },
                License = new OpenApiLicense
                {
                    Name = "Use under AGPLv3 License",
                    Url = new Uri("https://github.com/DroneDB/Registry/blob/master/LICENSE.md"),
                }
            });
            c.DocumentFilter<BasePathDocumentFilter>();
            c.OperationFilter<DownloadPathsParameterFilter>();

            // STAC and metadata endpoints return arbitrary JSON: a JsonElement or JsonNode is an open schema
            c.MapType<JsonElement>(() => new OpenApiSchema { Description = "Arbitrary JSON value." });
            c.MapType<JsonNode>(() => new OpenApiSchema { Description = "Arbitrary JSON value." });
            c.MapType<DatasetPath>(() => new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Description = "Dataset-relative path ('/' separated; absolute, '..' and reserved paths are refused)."
            });

            // Surface the generated XML documentation (controller actions + DTOs) in the API reference
            var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
            var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
            if (File.Exists(xmlPath))
                c.IncludeXmlComments(xmlPath, includeControllerXmlComments: true);
        });

        services.AddMvcCore()
            .AddApiExplorer();

        // One JSON configuration for the controllers and for the RFC 9457 writers (RQ-14, D3); the dataset path rule
        // applies to JSON bodies as it does to the query, the route and the form (RQ-11)
        services.AddOptions<Microsoft.AspNetCore.Mvc.JsonOptions>().Configure<DatasetPathPolicy>((options, paths) =>
            ApiJson.Configure(options.JsonSerializerOptions, paths));
        services.AddOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>().Configure<DatasetPathPolicy>((options, paths) =>
            ApiJson.Configure(options.SerializerOptions, paths));

        services.AddResponseCaching(options =>
        {
            options.MaximumBodySize = 8 * 1024 * 1024; // 8MB
            options.SizeLimit = 10 * 1024 * 1024; // 10MB
            options.UseCaseSensitivePaths = true;
        });

        // Dynamic compression for API responses (the Hub's static files are precompressed at build
        // time); also behind TLS termination. ZIP downloads are already compressed. Responses that
        // carry tokens are never compressed (CompressionPolicy, applied where the middleware runs).
        services.AddResponseCompression(options =>
        {
            options.EnableForHttps = true;
            options.MimeTypes = Microsoft.AspNetCore.ResponseCompression.ResponseCompressionDefaults.MimeTypes
                .Concat(["application/geo+json", "application/vnd.mapbox-vector-tile", "image/svg+xml"])
                .Where(t => t != "application/zip");
        });
        services.AddRegistryRateLimiting();

        return services;
    }

    /// <summary>
    /// The HTTP edge: CORS, the reverse proxy's forwarded headers, the multipart limit, the Hub's static
    /// files and the HTTP context accessor (registered once).
    /// </summary>
    public static IServiceCollection AddRegistryHttpHosting(this IServiceCollection services)
    {
        services.AddCors();
        services.AddSpaStaticFiles(config => { config.RootPath = MagicStrings.SpaRoot; });
        services.AddHttpContextAccessor();

        // Behind a reverse proxy the client address and scheme come from its forwarded headers
        services.AddOptions<ForwardedHeadersOptions>().Configure<IOptions<HttpHostOptions>>((options, http) =>
            ConfigureForwardedHeaders(options, http.Value));

        // See https://learn.microsoft.com/aspnet/core/fundamentals/servers/kestrel#maximum-client-connections
        services.AddOptions<FormOptions>().Configure<IOptions<HttpHostOptions>>((options, http) =>
        {
            if (http.Value.MaxRequestBodySize is { } max)
                options.MultipartBodyLengthLimit = max;
        });

        return services;
    }

    /// <summary>
    /// Trusts <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> only from the known proxies and peers
    /// inside the known networks (<see cref="TrustedProxies"/>; an invalid entry fails the start in the
    /// options validator). The framework defaults (loopback) are cleared first, so configured proxies
    /// replace them instead of adding to them. <c>ForwardLimit</c> stays 1: one proxy (nginx) in front of
    /// the app, so only the address it appended is used, never entries the client sent itself.
    /// </summary>
    internal static void ConfigureForwardedHeaders(ForwardedHeadersOptions options, HttpHostOptions http)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        foreach (var proxy in TrustedProxies.Proxies(http))
            options.KnownProxies.Add(proxy);
        foreach (var network in TrustedProxies.Networks(http))
            options.KnownIPNetworks.Add(network);
    }
}
