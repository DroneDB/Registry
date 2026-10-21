using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using Hangfire;
using Hangfire.Console;
using Hangfire.MySql;
using HealthChecks.UI.Client;
using Registry.Web.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Registry.Adapters;
using Registry.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Filters;
using Registry.Web.HealthChecks;
using Registry.Web.Middlewares;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Registry.Adapters.DroneDB;
using Registry.Adapters.Thumbnail;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Web.Services.Initialization;
using Registry.Web.Services.Managers.Wcs;
using Registry.Web.Utilities.Auth;
using Registry.Web.Utilities.Ogc;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Events;
using IMetaManager = Registry.Web.Services.Ports.IMetaManager;

namespace Registry.Web;

public class Startup
{
    private IConfiguration Configuration { get; }


    public Startup(IConfiguration configuration)
    {
        Configuration = configuration;
    }

    public void ConfigureServices(IServiceCollection services)
    {
        // libddb's lifecycle first: it starts before and stops after every other hosted service
        services.AddDdbRuntime<NativeDdbRuntime>(Configuration);

        services.AddCors();
        services.AddControllers(o => o.Filters.Add<ApiExceptionFilter>());

        // One error model (RQ-05, D1): RFC 9457 ProblemDetails written by ApiErrorResponder from the MVC
        // filter, the model-state factory below and the pipeline-wide exception handler
        // Every ProblemDetails carries noRetry, also the framework's own (e.g. a bare NotFound()); the
        // responder sets the real value of a classified error after this default
        services.AddProblemDetails(o => o.CustomizeProblemDetails = context =>
            context.ProblemDetails.Extensions.TryAdd(ApiErrorResponder.NoRetryMember, false));
        services.AddSingleton<ApiErrorResponder>();
        services.AddExceptionHandler<ApiExceptionHandler>();

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

            // Newtonsoft's JToken serializes to a self-referential schema (an array of JToken) that
            // makes the Scalar/OpenAPI viewer recurse infinitely ("Maximum call stack size exceeded").
            // STAC and metadata endpoints return arbitrary JSON, so represent JToken as an open schema.
            c.MapType<JToken>(() => new OpenApiSchema { Description = "Arbitrary JSON value." });

            // Surface the generated XML documentation (controller actions + DTOs) in the API reference
            var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
            var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
            if (File.Exists(xmlPath))
                c.IncludeXmlComments(xmlPath, includeControllerXmlComments: true);
        });

        services.AddMvcCore()
            .AddApiExplorer()
            .AddNewtonsoftJson(options =>
            {
                options.SerializerSettings.DateTimeZoneHandling = Newtonsoft.Json.DateTimeZoneHandling.Utc;
                options.SerializerSettings.DateFormatHandling = Newtonsoft.Json.DateFormatHandling.IsoDateFormat;
            });

        services.AddResponseCaching(options =>
        {
            options.MaximumBodySize = 8 * 1024 * 1024; // 8MB
            options.SizeLimit = 10 * 1024 * 1024; // 10MB
            options.UseCaseSensitivePaths = true;
        });

        services.AddSpaStaticFiles(config => { config.RootPath = "ClientApp"; });

        // Let's use a strongly typed class for settings
        var appSettingsSection = Configuration.GetSection("AppSettings");
        services.Configure<AppSettings>(appSettingsSection);
        var appSettings = appSettingsSection.Get<AppSettings>();

        services.AddDbContextWithProvider<ApplicationDbContext>(Configuration, appSettings.AuthProvider,
            MagicStrings.IdentityConnectionName, "Identity");

        if (appSettings.LdapSettings?.Enabled == true)
        {
            services.AddIdentityCore<User>()
                .AddUserManager<RegistryUserManager>()
                .AddRoles<IdentityRole>()
                .AddEntityFrameworkStores<ApplicationDbContext>()
                .AddDefaultTokenProviders();

            services.AddScoped<ILoginManager, LdapLoginManager>();
        }
        else if (!string.IsNullOrWhiteSpace(appSettings.ExternalAuthUrl))
        {
            services.AddIdentityCore<User>()
                .AddUserManager<RegistryUserManager>()
                .AddRoles<IdentityRole>()
                .AddEntityFrameworkStores<ApplicationDbContext>()
                .AddDefaultTokenProviders();

            services.AddScoped<ILoginManager, RemoteLoginManager>();
        }
        else
        {
            services.AddIdentityCore<User>()
                .AddUserManager<RegistryUserManager>()
                .AddRoles<IdentityRole>()
                .AddEntityFrameworkStores<ApplicationDbContext>()
                .AddSignInManager()
                .AddDefaultTokenProviders();

            services.AddScoped<ILoginManager, LocalLoginManager>();
        }

        services.AddDbContextWithProvider<RegistryContext>(Configuration, appSettings.RegistryProvider,
            MagicStrings.RegistryConnectionName, "Data");

        var key = Encoding.ASCII.GetBytes(appSettings.Secret);
        services.AddAuthentication(auth =>
            {
                auth.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                auth.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(jwt =>
            {
                jwt.RequireHttpsMetadata = false;
                jwt.SaveToken = true;
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(key),
                    ValidateIssuer = false,
                    ValidateAudience = false
                };
            });

        services.AddAuthorization(options => options.AddRegistryPolicies());
        services.AddScoped<IAuthorizationHandler, AdminAuthorizationHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, RegistryAuthorizationResultHandler>();

        services.Configure<IdentityOptions>(options =>
        {
            // Password settings - driven by PasswordPolicy configuration
            var passwordPolicy = appSettings.PasswordPolicy;
            if (passwordPolicy != null)
            {
                options.Password.RequireDigit = passwordPolicy.RequireDigit;
                options.Password.RequireLowercase = passwordPolicy.RequireLowercase;
                options.Password.RequireNonAlphanumeric = passwordPolicy.RequireNonAlphanumeric;
                options.Password.RequireUppercase = passwordPolicy.RequireUppercase;
                options.Password.RequiredLength = passwordPolicy.MinLength;
            }
            else
            {
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequireUppercase = false;
                options.Password.RequiredLength = 1;
            }
            options.Password.RequiredUniqueChars = 0;

            // Lockout settings.
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.AllowedForNewUsers = true;

            // User settings.
            options.User.AllowedUserNameCharacters =
                "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._@+";
            options.User.RequireUniqueEmail = false;
        });

        // Model validation errors: ValidationProblemDetails whose detail carries the field messages. The
        // responder is resolved per request: it needs ProblemDetailsFactory, which reads these options
        services.Configure<ApiBehaviorOptions>(o =>
        {
            o.InvalidModelStateResponseFactory = actionContext =>
                ApiErrorResponder.ToActionResult(actionContext.HttpContext.RequestServices
                    .GetRequiredService<ApiErrorResponder>()
                    .CreateValidation(actionContext.HttpContext, actionContext.ModelState));
        });

        services.AddMemoryCache();
        services.AddCacheProvider(appSettings);
        services.AddHangfireProvider(appSettings, Configuration);
        services.AddJobIndexing();

        var instanceType = Configuration.GetValue<InstanceType>("InstanceType");

        // If we are running both the webserver and the processing node
        if (instanceType == InstanceType.Default)
        {
            var budget = ProcessingBudget.Derive(HostFacts.Current(), appSettings.ProcessingBudget,
                appSettings.WorkerThreads);
            Log.Information("Processing budget: {Budget}", budget);
            services.AddBudgetedHangfireServers(budget);
        }

        var hcBuilder = services.AddHealthChecks()
            .AddCheck<CacheHealthCheck>("Cache health check", null, ["service"])
            .AddCheck<DdbHealthCheck>("DroneDB health check", null, ["service"])
            .AddCheck<UserManagerHealthCheck>("User manager health check", null, ["database"])
            .AddDbContextCheck<RegistryContext>("Registry database health check", null, ["database"])
            .AddDbContextCheck<ApplicationDbContext>("Registry identity database health check", null,
                ["database"])
            .AddDiskSpaceHealthCheck(appSettings.DatasetsPath, "Ddb datasets path space health check", null,
                ["storage"])
            .AddDiskSpaceHealthCheck(appSettings.CachePath, "Ddb cache path space health check", null,
                ["storage"])
            .AddDiskSpaceHealthCheck(appSettings.TempPath, "Ddb temp path space health check", null,
                ["storage"])
            .AddDiskSpaceHealthCheck(appSettings.StoragePath, "Ddb storage path space health check", null,
                ["storage"])
            .AddHangfire(options => { options.MinimumAvailableServers = 1; }, "Hangfire health check", null,
                ["processing"])
            .AddCheck<HangFireHealthCheck>("Hangfire processing health check", null, ["processing"])
            .AddCheck<ThumbnailGeneratorHealthCheck>("Thumbnail generator health check", null, ["service"]);

        if (appSettings.LdapSettings?.Enabled == true)
            hcBuilder.AddCheck<LdapHealthCheck>("LDAP health check", null, ["service"]);


        /*
         * NOTE about services lifetime:
         *
         * - A type should be registered as a "Singleton" only when it is fully thread-safe and is not dependent on other services or types.
         * - Scoped services are bound under a scope (request), and a new instance is created and reused inside a created "scope".
         * - If a service is defined as Transient, it is instantiated whenever invoked within a request.
         *   It is almost similar to creating an instance of the same type using "new" keyword and using it.
         *   It is also the safest option among all other service types, since we don't need to bother about the thread-safety and memory leaks.
         *
         * = In terms of lifetime, the singleton object gets the highest life per instantiation,
         *   followed by a Scoped service object and the least by a Transient object.
         */

        services.AddTransient<TokenManagerMiddleware>();
        services.AddTransient<JwtInCookieMiddleware>();
        services.AddTransient<UserEnrichmentMiddleware>();
        services.AddTransient<DownloadLimitMiddleware>();
        services.AddTransient<SecurityHeadersMiddleware>();
        services.AddSingleton<IDownloadLimiter, DownloadLimiter>();
        services.AddSingleton<IClientIpResolver, ClientIpResolver>();
        services.AddTransient<ITokenManager, TokenManager>();
        services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();

        services.AddScoped<IUtils, WebUtils>();
        services.AddSingleton<IDatasetLookupCache, DatasetLookupCache>();
        services.AddSingleton<Microsoft.EntityFrameworkCore.Diagnostics.ISaveChangesInterceptor, DatasetChangeInterceptor>();
        services.AddScoped<IDatasetSummaryProvider, DatasetSummaryProvider>();
        services.AddSingleton<UserAuthSnapshotCache>();
        services.AddScoped<IUserAuthSnapshotProvider, UserAuthSnapshotProvider>();
        services.AddScoped<IAuthManager, AuthManager>();
        services.AddScoped<IDatasetAccessResolver, DatasetAccessResolver>();

        services.AddScoped<IUsersManager, UsersManager>();
        services.AddScoped<IOrganizationsManager, OrganizationsManager>();
        services.AddScoped<IDatasetsManager, DatasetsManager>();
        services.AddScoped<IStacManager, StacManager>();
        services.AddScoped<IObjectsManager, ObjectsManager>();
        services.AddScoped<ICadCrsManager, CadCrsManager>();
        services.AddScoped<IShareManager, ShareManager>();
        services.AddScoped<IPushManager, PushManager>();
        services.AddSingleton<IPushSessionStore, PushSessionStore>();
        services.AddScoped<IDdbManager, DdbManager>();
        services.AddSingleton<InitializedDatasetFolders>();
        services.AddScoped<ISystemManager, SystemManager>();
        services.AddScoped<IRescanManager, RescanManager>();
        services.AddScoped<IBackgroundJobsProcessor, BackgroundJobsProcessor>();
        services.AddScoped<IMetaManager, Services.Managers.MetaManager>();
        services.AddScoped<BuildPendingService>();
        services.AddScoped<IBuildStatusService, BuildStatusService>();
        services.AddScoped<DatasetCleanupService>();
        services.AddScoped<OrphanedDatasetCleanupService>();
        services.AddScoped<RecurringDatasetCleanupService>();
        services.AddScoped<ArtifactCompletenessCheckerService>();
        services.AddScoped<IndexRescanService>();
        services.AddScoped<IndexReconciliationService>();

        // Admin global tasks dashboard manager (Part B).
        services.AddScoped<IAdminTasksManager, AdminTasksManager>();

        // Per-dataset Processing Platform task manager backing TasksController.
        services.AddScoped<ITasksManager, TasksManager>();

        // Processing Platform task substrate (native tools incl. build/raster-export) and the
        // web-only submission side (gating, quota, runner) used by the task/import managers
        services.AddProcessingPlatform();
        services.AddProcessingPlatformSubmission();

        // Import Dataset feature: source infrastructure + the import-dataset heavy tool (shared with
        // the worker host) and the web-only orchestration manager.
        services.AddImportSources(appSettings);
        services.AddScoped<IImportManager, ImportManager>();

        // Single-file URL import into an existing dataset (web-only orchestration; the import-file
        // heavy tool is registered by AddImportSources for both web and worker hosts).
        services.AddScoped<IFileUrlImportManager, FileUrlImportManager>();

        // Admin configuration editor: builds ConfigurationDataDto from AppSettings + defaults
        services.AddScoped<IConfigurationDataBuilder, ConfigurationDataBuilder>();

        services.AddScoped<BasicAuthFilter>();

        // OGC services (WMS/WMTS/WFS/WCS/OGC API + MVT endpoint)
        services.AddScoped<IBuildArtifactResolver, BuildArtifactResolver>();
        services.AddScoped<IOgcLayerCatalog, OgcLayerCatalog>();
        services.AddScoped<IOgcRasterTileSource, OgcRasterTileSource>();
        services.AddScoped<IWmtsManager, WmtsManager>();
        services.AddScoped<IWmsManager, WmsManager>();
        services.AddScoped<IWfsManager, WfsManager>();
        services.AddScoped<IWcsCoverageService, WcsCoverageService>();
        services.AddScoped<IWcsProtocolHandler, WcsProtocol10Handler>();
        services.AddScoped<IWcsProtocolHandler, WcsProtocol11Handler>();
        services.AddScoped<IWcsProtocolHandler, WcsProtocol20Handler>();
        services.AddScoped<IWcsManager, WcsManager>();
        services.AddScoped<IOgcApiFeaturesManager, OgcApiFeaturesManager>();
        services.AddScoped<IOgcApiTilesManager, OgcApiTilesManager>();
        services.AddScoped<OgcAuthorizationFilter>();
        services.AddScoped<OgcExceptionFilter>();

        // Native renders share a concurrency budget per lane (REG-07 §5.2); every thumbnail goes
        // through the Heavy lane, whichever generator makes it
        services.AddSingleton<INativeWorkGate, NativeWorkGate>();
        if (!string.IsNullOrWhiteSpace(appSettings.RemoteThumbnailGeneratorUrl))
        {
            services.Configure<RemoteThumbnailGeneratorSettings>(options =>
            {
                options.Url = appSettings.RemoteThumbnailGeneratorUrl;
                options.Token = appSettings.ThumbnailGeneratorToken;
            });
            services.AddSingleton<RemoteThumbnailGenerator>();
            services.AddSingleton<IThumbnailGenerator>(sp => new GatedThumbnailGenerator(
                sp.GetRequiredService<RemoteThumbnailGenerator>(), sp.GetRequiredService<INativeWorkGate>()));
        }
        else
        {
            services.AddSingleton<LocalThumbnailGenerator>();
            services.AddSingleton<IThumbnailGenerator>(sp => new GatedThumbnailGenerator(
                sp.GetRequiredService<LocalThumbnailGenerator>(), sp.GetRequiredService<INativeWorkGate>()));
        }

        // Upload pipeline (UPL-01): per-request context, quota reservations, receipts, coalesced
        // invalidation and bounded thumbnail pre-generation
        services.AddScoped<IUploadManager, UploadManager>();
        services.AddScoped<IChunkedUploadManager, ChunkedUploadManager>();
        services.AddSingleton<IChunkStore, ChunkStore>();
        services.AddSingleton<UploadQuotaLedger>();
        services.AddScoped<IUploadQuotaGuard, UploadQuotaGuard>();
        services.AddSingleton<IUploadReceiptStore, DistributedUploadReceiptStore>();
        services.AddSingleton<DatasetChangeCoalescer>();
        services.AddSingleton<IDatasetChangeCoalescer>(sp => sp.GetRequiredService<DatasetChangeCoalescer>());
        services.AddHostedService(sp => sp.GetRequiredService<DatasetChangeCoalescer>());
        services.AddSingleton<ThumbnailPrecomputeQueue>();
        services.AddSingleton<IThumbnailPrecomputeQueue>(sp => sp.GetRequiredService<ThumbnailPrecomputeQueue>());
        services.AddHostedService(sp => sp.GetRequiredService<ThumbnailPrecomputeQueue>());

        services.AddSingleton<IFileSystem, FileSystem>();

        // To change
        services.AddSingleton<IDdbWrapper, NativeDdbWrapper>();

        services.AddSingleton<IBatchTokenGenerator, BatchTokenGenerator>();
        services.AddSingleton<INameGenerator, NameGenerator>();
        services.AddSingleton<ICacheManager, CacheManager>();
        services.AddSingleton<IPasswordPolicyValidator, PasswordPolicyValidator>();
        services.AddSingleton<IDatasetIndexQueue, DatasetIndexQueue>();

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

        // Behind a reverse proxy the client address and scheme come from its forwarded headers
        var knownProxies = ParseKnownProxies(appSettings.EffectiveKnownProxies());
        var knownNetworks = ParseKnownNetworks(appSettings.KnownNetworks);
        services.Configure<ForwardedHeadersOptions>(options =>
            ConfigureForwardedHeaders(options, knownProxies, knownNetworks));

        if (appSettings.MaxRequestBodySize.HasValue)
        {
            services.Configure<FormOptions>(options =>
            {
                // See https://docs.microsoft.com/it-it/aspnet/core/fundamentals/servers/kestrel?view=aspnetcore-3.1#maximum-client-connections
                // We could put this in config "Kestrel->Limits" section
                options.MultipartBodyLengthLimit = appSettings.MaxRequestBodySize.Value;
            });
        }

        services.AddHttpContextAccessor();
        services.AddHttpClient();

        // TODO: Enable when needed. Should check return object structure
        // services.AddOData();

        if (appSettings.WorkerThreads > 0)
        {
            ThreadPool.GetMinThreads(out _, out var ioCompletionThreads);
            ThreadPool.SetMinThreads(appSettings.WorkerThreads, ioCompletionThreads);
        }

        // Application initializer: runs in IHostedLifecycleService.StartingAsync, so before every
        // hosted service registered above starts, regardless of this position
        services.AddHostedService<AppInitializer>();
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env, ILogger<Startup> logger)
    {
        // First: everything after it (HSTS, logging, auth cookies, limits) sees the real client
        // address and scheme, taken from the headers of a known proxy only
        app.UseForwardedHeaders();

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

        var corsSettings = app.ApplicationServices.GetRequiredService<IOptions<AppSettings>>().Value;
        if (corsSettings.AllowedOrigins is { Length: > 0 })
        {
            app.UseCors(cors => cors
                .WithOrigins(corsSettings.AllowedOrigins)
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
            // dashboard itself; an unknown dashboard page is Hangfire's 404 (it was a 500 while the
            // middleware form was registered too, RQ-04 finding)
            endpoints.MapHangfireDashboard(MagicStrings.HangFireUrl, new DashboardOptions
            {
                AsyncAuthorization = [new HangfireAuthorizationFilter()]
            }).RequireAuthorization();

            // TODO: Enable when needed
            // endpoints.MapODataRoute("odata", "odata", GetEdmModel());
        });

        app.UseWhen(context => !context.Request.Path.StartsWithSegments("/static"), builder =>
        {
            // Serve user-supplied branding assets (logos, favicons, manifest)
            // from {StoragePath}/branding/ at /branding/*. This folder is
            // preserved across Hub upgrades.
            var brandingRoot = Path.GetFullPath(
                Path.Combine(corsSettings.StoragePath ?? ".", MagicStrings.BrandingFolder));
            Directory.CreateDirectory(brandingRoot);
            builder.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(brandingRoot),
                RequestPath = MagicStrings.BrandingUrlPrefix,
                ServeUnknownFileTypes = true
            });

            // Build-time .br/.gz twins of the Hub's JS/CSS (HUB-09)
            var spaFiles = builder.ApplicationServices
                .GetService<Microsoft.AspNetCore.SpaServices.StaticFiles.ISpaStaticFileProvider>();
            builder.UseMiddleware<PrecompressedStaticFilesMiddleware>(
                new Func<Microsoft.Extensions.FileProviders.IFileProvider>(() => spaFiles?.FileProvider));

            // Hashed files immutable, index.html revalidated (HUB-09)
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

        // Application initialization is now handled by AppInitializer IHostedService
        // Database setup, cache validation, cache registration, and Hangfire jobs are configured on startup

        PrintStartupInfo(app);
    }

    /// <summary>
    /// Trusts <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> only from <paramref name="knownProxies"/>
    /// and peers inside <paramref name="knownNetworks"/>. The framework defaults (loopback) are cleared
    /// first, so configured proxies replace them instead of adding to them. <c>ForwardLimit</c> stays 1:
    /// one proxy (nginx) in front of the app, so only the address it appended is used, never entries the
    /// client sent itself.
    /// </summary>
    private static void ConfigureForwardedHeaders(ForwardedHeadersOptions options,
        IReadOnlyList<IPAddress> knownProxies, IReadOnlyList<System.Net.IPNetwork> knownNetworks)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        foreach (var proxy in knownProxies)
            options.KnownProxies.Add(proxy);
        foreach (var network in knownNetworks)
            options.KnownIPNetworks.Add(network);
    }

    /// <summary>
    /// <see cref="AppSettings.EffectiveKnownProxies"/> (the default applies when not set) as addresses.
    /// </summary>
    private static IPAddress[] ParseKnownProxies(string[] effective) =>
        ParseTrustedEntries<IPAddress>(effective,
            nameof(AppSettings.KnownProxies), "IP address", IPAddress.TryParse);

    /// <summary>
    /// <see cref="AppSettings.KnownNetworks"/> as CIDR networks (none when not set).
    /// </summary>
    private static System.Net.IPNetwork[] ParseKnownNetworks(string[] configured) =>
        ParseTrustedEntries<System.Net.IPNetwork>(configured ?? [],
            nameof(AppSettings.KnownNetworks), "CIDR network", System.Net.IPNetwork.TryParse);

    private delegate bool TryParseEntry<T>(string entry, out T value);

    /// <summary>
    /// Parses the trusted-proxy entries of the <c>AppSettings:<paramref name="setting"/></c> list. An
    /// invalid entry stops the start-up: ignoring it would silently stop trusting the proxy and key
    /// every client on the proxy address.
    /// </summary>
    private static T[] ParseTrustedEntries<T>(IEnumerable<string> entries, string setting, string kind,
        TryParseEntry<T> tryParse) =>
        entries
            .Select(entry => entry != null && tryParse(entry.Trim(), out var value)
                ? value
                : throw new InvalidOperationException(
                    $"AppSettings:{setting} contains an invalid {kind}: '{entry}'"))
            .ToArray();

    private static void PrintStartupInfo(IApplicationBuilder app)
    {
        if (Log.IsEnabled(LogEventLevel.Information)) return;

        var env = app.ApplicationServices.GetService<IHostEnvironment>();
        var serverAddresses = app.ServerFeatures.Get<IServerAddressesFeature>()?.Addresses;
        var settingsOptions = app.ApplicationServices.GetService<IOptions<AppSettings>>();

        if (settingsOptions == null)
            throw new InvalidOperationException("AppSettings not found");

        WriteStartupInfo(Console.Out, env?.EnvironmentName, serverAddresses, settingsOptions.Value);
    }

    /// <summary>
    /// Writes the start-up banner (mode, version, URLs, useful links) to <paramref name="output"/>.
    /// Secrets (admin password, MonitorToken) are never written: stdout ends up in container logs.
    /// </summary>
    internal static void WriteStartupInfo(TextWriter output, string environmentName,
        ICollection<string> serverAddresses, AppSettings settings)
    {
        output.WriteLine(" -> Application started in {0} mode", environmentName ?? "unknown");
        output.WriteLine(" ?> Version: {0}", Assembly.GetExecutingAssembly().GetName().Version);
        output.WriteLine(" ?> Application started at {0}", DateTime.Now);

        if (serverAddresses != null)
        {
            foreach (var address in serverAddresses)
            {
                var addr = address.Replace("0.0.0.0", "localhost");
                output.WriteLine($" ?> Registry url: {addr}");
            }
        }

        if (settings.DefaultAdmin == null)
            throw new InvalidOperationException("DefaultAdmin not found");

        output.WriteLine(" ?> Default admin username: {0}", settings.DefaultAdmin.UserName);

        var url = serverAddresses?.FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(settings.ExternalUrlOverride))
        {
            output.WriteLine($" ?> External URL: {settings.ExternalUrlOverride}");
            url = settings.ExternalUrlOverride;
        }

        if (url != null)
        {
            var builder = new UriBuilder(url);

            if (builder.Host == "0.0.0.0") builder.Host = "localhost";
            var baseUri = builder.Uri;
            // Health links need the MonitorToken (?token=...) or an admin login: the token is not printed
            const string reqAuth = "(req auth)";

            output.WriteLine();
            output.WriteLine(" ?> Useful links:");

            var scalarUri = new Uri(baseUri, MagicStrings.ScalarUrl);
            output.WriteLine($" ?> API Docs (Scalar): {scalarUri}");

            var versionUri = new Uri(baseUri, MagicStrings.VersionUrl);
            output.WriteLine($" ?> Version: {versionUri}");

            var quickHealthUri = new Uri(baseUri, MagicStrings.QuickHealthUrl);
            output.WriteLine($" ?> {reqAuth} Quick Health: {quickHealthUri}");

            var healthUri = new Uri(baseUri, MagicStrings.HealthUrl);
            output.WriteLine($" ?> {reqAuth} Health: {healthUri}");

            var hangfireUri = new Uri(baseUri, MagicStrings.HangFireUrl);
            output.WriteLine($" ?> (req auth) Hangfire: {hangfireUri}");

            output.WriteLine();
        }

        output.WriteLine(" ?> Press Ctrl+C to quit");
    }
}