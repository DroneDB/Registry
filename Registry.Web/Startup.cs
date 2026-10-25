using System;
using System.IO;
using System.Reflection;
using Hangfire;
using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Web.Filters;
using Registry.Web.Hosting;
using Registry.Web.Middlewares;
using Registry.Web.Models.Configuration;
using Registry.Web.Utilities;
using Registry.Web.Utilities.Auth;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Events;

namespace Registry.Web;

public class Startup
{
    private IConfiguration Configuration { get; }


    public Startup(IConfiguration configuration)
    {
        Configuration = configuration;
    }

    /// <summary>
    /// The web host's services: the shared job services (<c>AddProcessingNodeServices</c>, the base of
    /// every job host), the job storage, and the web modules. The libddb runtime, the options and the
    /// startup task runner are registered by <see cref="RegistryHost.CreateBuilder"/> before this runs.
    /// </summary>
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddProcessingNodeServices(Configuration);
        services.AddRegistryJobs(Configuration);

        // The all-in-one instance runs the jobs too
        if (Configuration.GetInstanceType() == InstanceType.Default)
            services.AddJobServers();

        services.AddRegistryApi();
        services.AddRegistryHttpHosting();
        services.AddRegistryAuth(Configuration);
        services.AddRegistryDomain();
        services.AddRegistryOgc();
        services.AddRegistryThumbnails(Configuration);
        services.AddUploadPipeline();
        services.AddRegistryHealthChecks(Configuration);
        services.AddRegistryWebStartup();
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env, IHostApplicationLifetime lifetime,
        ILoggerFactory loggerFactory, IOptions<HttpHostOptions> http, IOptions<StorageOptions> storage,
        IOptions<IdentityProviderOptions> identity)
    {
        var logger = loggerFactory.CreateLogger(RegistryHost.StartupLogCategory);

        // First: everything after it (HSTS, logging, auth cookies, limits) sees the real client
        // address and scheme, taken from the headers of a known proxy only
        app.UseForwardedHeaders();

        // Every native call made while a request is served logs with that request's id
        app.UseDdbRequestOperation();

        // Every error below answers ProblemDetails (ApiExceptionHandler), in every environment: the
        // developer exception page would give the Hub an HTML body in Development only
        app.UseExceptionHandler();

        if (!env.IsDevelopment())
            app.UseHsts();

        app.UseSerilogRequestLogging(options =>
        {
            // Customize the message template
            options.MessageTemplate = "Handled {RequestPath}";

            // Emit debug-level events instead of the defaults
            options.GetLevel = (httpContext, elapsed, ex) => LogEventLevel.Debug;

            // Attach additional properties to the request completion event
            options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
            {
                diagnosticContext.Set("RequestHost", httpContext.Request.Host.Value);
                diagnosticContext.Set("RequestScheme", httpContext.Request.Scheme);
            };
        });

        app.UseDefaultFiles();

        // Generate OpenAPI document via Swashbuckle
        app.UseSwagger();

        // Redirect /swagger to /scalar/v1 for backwards compatibility
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/swagger"))
            {
                context.Response.Redirect("/scalar/v1");
                return;
            }
            await next(context);
        });

        app.UseRouting();

        if (http.Value.AllowedOrigins is { Length: > 0 } allowedOrigins)
        {
            app.UseCors(cors => cors
                .WithOrigins(allowedOrigins)
                .AllowAnyMethod()
                .AllowAnyHeader());
        }
        else
        {
            logger.LogWarning("CORS is configured to allow any origin. Consider setting AllowedOrigins in appsettings for production environments");

            app.UseCors(cors => cors
                .AllowAnyOrigin()
                .AllowAnyMethod()
                .AllowAnyHeader());
        }

        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseMiddleware<JwtInCookieMiddleware>();

        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();

        // STAC API responses must be served uncompressed: STAC validators (pystac/urllib)
        // send "Accept-Encoding: *" but cannot decode gzip/brotli, which breaks catalog
        // traversal and JSON parsing. Strip Accept-Encoding for /stac so responses stay identity.
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/stac"))
                context.Request.Headers.Remove("Accept-Encoding");
            await next();
        });

        // Not for responses that carry tokens (BREACH, CompressionPolicy)
        app.UseWhen(context => !CompressionPolicy.CarriesSecrets(context.Request.Path),
            compressed => compressed.UseResponseCompression());
        app.UseResponseCaching();

        app.UseMiddleware<TokenManagerMiddleware>();
        app.UseMiddleware<UserEnrichmentMiddleware>();
        app.UseMiddleware<DownloadLimitMiddleware>();

        app.UseEndpoints(endpoints =>
        {
            endpoints.MapScalarApiReference(options =>
            {
                options.WithOpenApiRoutePattern("/swagger/v1/swagger.json");
            });
            endpoints.MapControllers();

            endpoints.MapHealthChecks(MagicStrings.QuickHealthUrl, new HealthCheckOptions
            {
                Predicate = _ => false
            }).RequireAdminOrMonitorToken();

            endpoints.MapHealthChecks(MagicStrings.HealthUrl, new HealthCheckOptions
            {
                ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
            }).RequireAdminOrMonitorToken();

            endpoints.MapLiveness();

            endpoints.MapGet(MagicStrings.VersionUrl,
                async context =>
                {
                    await context.Response.WriteAsync(
                        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "undefined");
                });

            // Registered once, as an endpoint: signed-in callers only, then the admin check of the
            // dashboard itself; an unknown dashboard page is Hangfire's 404 (registering the middleware form too
            // made it a 500)
            endpoints.MapHangfireDashboard(MagicStrings.HangFireUrl, new DashboardOptions
            {
                AsyncAuthorization = [new HangfireAuthorizationFilter()]
            }).RequireAuthorization();
        });

        app.UseWhen(context => !context.Request.Path.StartsWithSegments("/static"), builder =>
        {
            // Serve user-supplied branding assets (logos, favicons, manifest)
            // from {StoragePath}/branding/ at /branding/*. This folder is
            // preserved across Hub upgrades.
            var brandingRoot = Path.GetFullPath(
                Path.Combine(storage.Value.StoragePath ?? ".", MagicStrings.BrandingFolder));
            Directory.CreateDirectory(brandingRoot);
            builder.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(brandingRoot),
                RequestPath = MagicStrings.BrandingUrlPrefix,
                ServeUnknownFileTypes = true
            });

            // Build-time .br/.gz twins of the Hub's JS/CSS
            var spaFiles = builder.ApplicationServices
                .GetService<Microsoft.AspNetCore.SpaServices.StaticFiles.ISpaStaticFileProvider>();
            builder.UseMiddleware<PrecompressedStaticFilesMiddleware>(
                new Func<Microsoft.Extensions.FileProviders.IFileProvider>(() => spaFiles?.FileProvider));

            // Hashed files immutable, index.html revalidated
            var hubStaticFiles = HubCachePolicy.StaticFileOptions();
            builder.UseSpaStaticFiles(hubStaticFiles);

            builder.UseSpa(spa =>
            {
                // To learn more about options for serving an Angular SPA from ASP.NET Core,
                // see https://go.microsoft.com/fwlink/?linkid=864501
                spa.Options.SourcePath = "ClientApp";
                // Client routes (/login, /r/...) answer with index.html: same headers as /index.html
                spa.Options.DefaultPageStaticFileOptions = hubStaticFiles;

                if (env.IsDevelopment())
                {
                }
            });
        });

        // The banner once the server listens (its addresses are known then)
        lifetime.ApplicationStarted.Register(() =>
        {
            foreach (var line in StartupBanner.Lines(env.EnvironmentName,
                         app.ServerFeatures.Get<IServerAddressesFeature>()?.Addresses, http.Value, identity.Value))
                logger.LogInformation("{StartupBanner:l}", line);
        });
    }
}