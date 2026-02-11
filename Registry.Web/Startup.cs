using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
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
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
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
using Registry.Web.Utilities.Auth;
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
        services.AddCors();
        services.AddControllers();

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

        if (!string.IsNullOrWhiteSpace(appSettings.ExternalAuthUrl))
        {
            services.AddIdentityCore<User>()
                .AddRoles<IdentityRole>()
                .AddEntityFrameworkStores<ApplicationDbContext>()
                .AddDefaultTokenProviders();

            services.AddScoped<ILoginManager, RemoteLoginManager>();
        }
        else
        {
            services.AddIdentityCore<User>()
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

        services.Configure<IdentityOptions>(options =>
        {
            // Password settings.
            options.Password.RequireDigit = false;
            options.Password.RequireLowercase = false;
            options.Password.RequireNonAlphanumeric = false;
            options.Password.RequireUppercase = false;
            options.Password.RequiredLength = 1;
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

        // Error messages that make sense
        services.Configure<ApiBehaviorOptions>(o =>
        {
            o.InvalidModelStateResponseFactory = actionContext =>
                new BadRequestObjectResult(new ErrorResponse(actionContext.ModelState));
        });

        services.AddMemoryCache();
        services.AddCacheProvider(appSettings);
        services.AddHangfireProvider(appSettings, Configuration);
        services.AddJobIndexing();

        var instanceType = Configuration.GetValue<InstanceType>("InstanceType");

        // If we are running both the webserver and the processing node
        if (instanceType == InstanceType.Default)
        {
            var workers = appSettings.WorkerThreads > 0 ? appSettings.WorkerThreads : Environment.ProcessorCount;
            services.AddHangfireServer(options => { options.WorkerCount = workers; });
        }

        services.AddHealthChecks()
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
        services.AddTransient<ITokenManager, TokenManager>();
        services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();

        services.AddScoped<IUtils, WebUtils>();
        services.AddScoped<IAuthManager, AuthManager>();

        services.AddScoped<IUsersManager, UsersManager>();
        services.AddScoped<IOrganizationsManager, OrganizationsManager>();
        services.AddScoped<IDatasetsManager, DatasetsManager>();
        services.AddScoped<IStacManager, StacManager>();
        services.AddScoped<IObjectsManager, ObjectsManager>();
        services.AddScoped<IShareManager, ShareManager>();
        services.AddScoped<IPushManager, PushManager>();
        services.AddScoped<IDdbManager, DdbManager>();
        services.AddScoped<ISystemManager, SystemManager>();
        services.AddScoped<IBackgroundJobsProcessor, BackgroundJobsProcessor>();
        services.AddScoped<IMetaManager, Services.Managers.MetaManager>();
        services.AddScoped<BuildPendingService>();
        services.AddScoped<DatasetCleanupService>();
        services.AddScoped<OrphanedDatasetCleanupService>();

        services.AddScoped<IConfigurationHelper<AppSettings>, ConfigurationHelper>(_ =>
            new ConfigurationHelper(MagicStrings.AppSettingsFileName));

        services.AddScoped<BasicAuthFilter>();

        if (!string.IsNullOrWhiteSpace(appSettings.RemoteThumbnailGeneratorUrl))
        {
            services.Configure<RemoteThumbnailGeneratorSettings>(options =>
                options.Url = appSettings.RemoteThumbnailGeneratorUrl);
            services.AddSingleton<IThumbnailGenerator, RemoteThumbnailGenerator>();
        }
        else
        {
            services.AddSingleton<IThumbnailGenerator, LocalThumbnailGenerator>();
        }

        services.AddSingleton<IFileSystem, FileSystem>();

        // To change
        services.AddSingleton<IDdbWrapper, NativeDdbWrapper>();

        services.AddSingleton<IPasswordHasher, PasswordHasher>();
        services.AddSingleton<IBatchTokenGenerator, BatchTokenGenerator>();
        services.AddSingleton<INameGenerator, NameGenerator>();
        services.AddSingleton<ICacheManager, CacheManager>();
        services.AddSingleton<IPasswordPolicyValidator, PasswordPolicyValidator>();

        services.AddResponseCompression();

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

        // Register application initializer
        services.AddHostedService<AppInitializer>();
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        if (env.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }
        else
        {
            app.UseHsts();
        }

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
            await next();
        });

        app.UseRouting();

        // We are permissive now
        app.UseCors(cors => cors
            .AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader());

        app.UseMiddleware<JwtInCookieMiddleware>();

        app.UseAuthentication();
        app.UseAuthorization();

        app.UseResponseCompression();
        app.UseResponseCaching();

        app.UseMiddleware<TokenManagerMiddleware>();
        app.UseMiddleware<UserEnrichmentMiddleware>();

        app.UseHangfireDashboard(MagicStrings.HangFireUrl, new DashboardOptions
        {
            AsyncAuthorization = [new HangfireAuthorizationFilter()]
        });

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

            endpoints.MapGet(MagicStrings.VersionUrl,
                async context =>
                {
                    await context.Response.WriteAsync(
                        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "undefined");
                });

            endpoints.MapHangfireDashboard().RequireAuthorization();

            // TODO: Enable when needed
            // endpoints.MapODataRoute("odata", "odata", GetEdmModel());
        });

        app.UseWhen(context => !context.Request.Path.StartsWithSegments("/static"), builder =>
        {
            builder.UseSpaStaticFiles(new StaticFileOptions
            {
                ServeUnknownFileTypes = true,
            });

            builder.UseSpa(spa =>
            {
                // To learn more about options for serving an Angular SPA from ASP.NET Core,
                // see https://go.microsoft.com/fwlink/?linkid=864501
                spa.Options.SourcePath = "ClientApp";

                if (env.IsDevelopment())
                {
                }
            });
        });

        // Application initialization is now handled by AppInitializer IHostedService
        // Database setup, cache validation, cache registration, and Hangfire jobs are configured on startup

        PrintStartupInfo(app);
    }

    private static void PrintStartupInfo(IApplicationBuilder app)
    {
        if (Log.IsEnabled(LogEventLevel.Information)) return;

        var env = app.ApplicationServices.GetService<IHostEnvironment>();
        Console.WriteLine(" -> Application started in {0} mode", env?.EnvironmentName ?? "unknown");
        Console.WriteLine(" ?> Version: {0}", Assembly.GetExecutingAssembly().GetName().Version);
        Console.WriteLine(" ?> Application started at {0}", DateTime.Now);

        var serverAddresses = app.ServerFeatures.Get<IServerAddressesFeature>()?.Addresses;
        if (serverAddresses != null)
        {
            foreach (var address in serverAddresses)
            {
                var addr = address.Replace("0.0.0.0", "localhost");
                Console.WriteLine($" ?> Registry url: {addr}");
            }
        }

        var settingsOptions = app.ApplicationServices.GetService<IOptions<AppSettings>>();

        if (settingsOptions == null)
            throw new InvalidOperationException("AppSettings not found");

        var settings = settingsOptions.Value;

        if (settings.DefaultAdmin == null)
            throw new InvalidOperationException("DefaultAdmin not found");

        Console.WriteLine(" ?> Admin credentials: ");
        Console.WriteLine(" ?> Username: {0}", settings.DefaultAdmin.UserName);
        Console.WriteLine(" ?> Password: {0}", settings.DefaultAdmin.Password);

        var url = serverAddresses?.FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(settings.ExternalUrlOverride))
        {
            Console.WriteLine($" ?> External URL: {settings.ExternalUrlOverride}");
            url = settings.ExternalUrlOverride;
        }

        if (url != null)
        {
            var builder = new UriBuilder(url);

            if (builder.Host == "0.0.0.0") builder.Host = "localhost";
            var baseUri = builder.Uri;
            var monitorQuery = string.IsNullOrWhiteSpace(settings.MonitorToken)
                ? string.Empty
                : $"?token={settings.MonitorToken}";
            var reqAuth = string.IsNullOrWhiteSpace(settings.MonitorToken) ? "(req auth)" : string.Empty;

            Console.WriteLine();
            Console.WriteLine(" ?> Useful links:");

            var scalarUri = new Uri(baseUri, MagicStrings.ScalarUrl);
            Console.WriteLine($" ?> API Docs (Scalar): {scalarUri}");

            var versionUri = new Uri(baseUri, MagicStrings.VersionUrl);
            Console.WriteLine($" ?> Version: {versionUri}");

            var quickHealthUri = new Uri(baseUri, MagicStrings.QuickHealthUrl);
            Console.WriteLine($" ?> {reqAuth} Quick Health: {quickHealthUri}{monitorQuery}");

            var healthUri = new Uri(baseUri, MagicStrings.HealthUrl);
            Console.WriteLine($" ?> {reqAuth} Health: {healthUri}{monitorQuery}");

            var hangfireUri = new Uri(baseUri, MagicStrings.HangFireUrl);
            Console.WriteLine($" ?> (req auth) Hangfire: {hangfireUri}");

            Console.WriteLine();
        }

        Console.WriteLine(" ?> Press Ctrl+C to quit");
    }
}