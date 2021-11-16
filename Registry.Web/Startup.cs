using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Caching;
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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Newtonsoft.Json.Serialization;
using Registry.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Filters;
using Registry.Web.HealthChecks;
using Registry.Web.Middlewares;
using Registry.Web.Models.Configuration;
using Registry.Web.Services;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web
{
    public class Startup
    {
        private const string IdentityConnectionName = "IdentityConnection";
        private const string RegistryConnectionName = "RegistryConnection";
        private const string HangfireConnectionName = "HangfireConnection";

        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

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
                        Name = "Luca Di Leo",
                        Email = "ldileo@digipa.it",
                        Url = new Uri("https://digipa.it/"),
                    },
                    License = new OpenApiLicense
                    {
                        Name = "Use under AGPLv3 License",
                        Url = new Uri("https://github.com/DroneDB/Registry/blob/master/LICENSE.md"),
                    }
                });
                c.DocumentFilter<BasePathDocumentFilter>();
            });
            services.AddSwaggerGenNewtonsoftSupport();

            services.AddMvcCore()
                .AddApiExplorer()
                .AddNewtonsoftJson();

            services.AddResponseCaching(options =>
            {
                options.MaximumBodySize = 8 * 1024 * 1024; // 8MB
                options.SizeLimit = 10 * 1024 * 1024; // 10MB
                options.UseCaseSensitivePaths = true;
            });

            services.AddSpaStaticFiles(config => { config.RootPath = "ClientApp/build"; });

            // Let's use a strongly typed class for settings
            var appSettingsSection = Configuration.GetSection("AppSettings");
            services.Configure<AppSettings>(appSettingsSection);
            var appSettings = appSettingsSection.Get<AppSettings>();

            ConfigureDbProvider<ApplicationDbContext>(services, appSettings.AuthProvider, IdentityConnectionName);

            if (!string.IsNullOrWhiteSpace(appSettings.ExternalAuthUrl))
            {
                services.AddIdentityCore<User>()
                    .AddRoles<IdentityRole>()
                    .AddEntityFrameworkStores<ApplicationDbContext>();

                services.AddScoped<ILoginManager, RemoteLoginManager>();
            }
            else
            {
                services.AddIdentityCore<User>()
                    .AddRoles<IdentityRole>()
                    .AddEntityFrameworkStores<ApplicationDbContext>()
                    .AddSignInManager();

                services.AddScoped<ILoginManager, LocalLoginManager>();
            }

            ConfigureDbProvider<RegistryContext>(services, appSettings.RegistryProvider, RegistryConnectionName);

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
            RegisterCacheProvider(services, appSettings);
            RegisterHangfireProvider(services, appSettings);

            services.AddHealthChecks()
                .AddCheck<CacheHealthCheck>("Cache health check", null, new[] { "service" })
                .AddCheck<DdbHealthCheck>("DroneDB health check", null, new[] { "service" })
                .AddCheck<UserManagerHealthCheck>("User manager health check", null, new[] { "database" })
                .AddDbContextCheck<RegistryContext>("Registry database health check", null, new[] { "database" })
                .AddDbContextCheck<ApplicationDbContext>("Registry identity database health check", null,
                    new[] { "database" })
                .AddDiskSpaceHealthCheck(appSettings.StoragePath, "Ddb storage path space health check", null,
                    new[] { "storage" })
                .AddHangfire(options => { options.MinimumAvailableServers = 1; }, "Hangfire health check", null,
                    new[] { "database" });

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
            services.AddTransient<ITokenManager, TokenManager>();
            services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();

            services.AddScoped<IUtils, WebUtils>();
            services.AddScoped<IAuthManager, AuthManager>();

            services.AddScoped<IUsersManager, UsersManager>();
            services.AddScoped<IOrganizationsManager, OrganizationsManager>();
            services.AddScoped<IDatasetsManager, DatasetsManager>();
            services.AddScoped<IObjectsManager, ObjectsManager>();
            services.AddScoped<IShareManager, ShareManager>();
            services.AddScoped<IPushManager, PushManager>();
            services.AddScoped<IDdbManager, DdbManager>();
            services.AddScoped<ISystemManager, SystemManager>();
            services.AddScoped<IBackgroundJobsProcessor, BackgroundJobsProcessor>();
            services.AddScoped<IMetaManager, MetaManager>();
            services.AddScoped<IS3BridgeManager, S3BridgeManager>();

            services.AddSingleton<IPasswordHasher, PasswordHasher>();
            services.AddSingleton<IBatchTokenGenerator, BatchTokenGenerator>();
            services.AddSingleton<INameGenerator, NameGenerator>();
            services.AddSingleton<ICacheManager, CacheManager>();
            services.AddSingleton<ObjectCache>(provider => new FileCache(FileCacheManagers.Hashed, 
                appSettings.BridgeCachePath, new DefaultSerializationBinder(), 
                true, appSettings.ClearCacheInterval ?? default)
            {
                PayloadReadMode = FileCache.PayloadMode.Filename,
                PayloadWriteMode = FileCache.PayloadMode.Filename
            });

            //RegisterStorageProvider(services, appSettings);

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

            // If using Kestrel:
            services.Configure<KestrelServerOptions>(options => { options.AllowSynchronousIO = true; });

            // If using IIS:
            services.Configure<IISServerOptions>(options => { options.AllowSynchronousIO = true; });

            // TODO: Enable when needed. Should check return object structure
            // services.AddOData();

            if (appSettings.WorkerThreads > 0)
            {
                ThreadPool.GetMinThreads(out _, out var ioCompletionThreads);
                ThreadPool.SetMinThreads(appSettings.WorkerThreads, ioCompletionThreads);
            }
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

            app.UseDefaultFiles();

            app.UseSwagger();
            app.UseSwaggerUI(c => { c.SwaggerEndpoint("/swagger/v1/swagger.json", "Registry API"); });

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

            app.UseHangfireDashboard("/hangfire", new DashboardOptions
            {
                AsyncAuthorization = new[] { new HangfireAuthorizationFilter() }
            });
            
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();

                endpoints.MapHealthChecks("/quickhealth", new HealthCheckOptions
                {
                    Predicate = _ => false
                }).RequireAuthorization();

                endpoints.MapHealthChecks("/health", new HealthCheckOptions
                {
                    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
                }).RequireAuthorization();

                endpoints.MapGet("/version",
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

            SetupDatabase(app);
            //SetupHangfire(app);
        }

        // private void SetupHangfire(IApplicationBuilder app)
        // {
        //     var appSettingsSection = Configuration.GetSection("AppSettings");
        //     var appSettings = appSettingsSection.Get<AppSettings>();
        //     
        //     if (appSettings.StorageCleanupMinutes is > 0)
        //     {
        //         using var serviceScope = app.ApplicationServices
        //             .GetRequiredService<IServiceScopeFactory>()
        //             .CreateScope();
        //     
        //         var objectSystem = serviceScope.ServiceProvider.GetService<IObjectSystem>();
        //         
        //         RecurringJob.AddOrUpdate(MagicStrings.StorageCleanupJobId, () =>
        //             HangfireUtils.SyncAndCleanupWrapper(objectSystem, null),
        //             $"*/{appSettings.StorageCleanupMinutes} * * * *");
        //
        //     }
        //     else
        //     {
        //         RecurringJob.RemoveIfExists(MagicStrings.StorageCleanupJobId);
        //     }
        // }

        // NOTE: Maybe put all this as stated in https://stackoverflow.com/a/55707949
        private void SetupDatabase(IApplicationBuilder app)
        {
            using var serviceScope = app.ApplicationServices
                .GetRequiredService<IServiceScopeFactory>()
                .CreateScope();
            using var applicationDbContext = serviceScope.ServiceProvider.GetService<ApplicationDbContext>();

            if (applicationDbContext == null)
                throw new InvalidOperationException("Cannot get application db context from service provider");

            if (applicationDbContext.Database.IsSqlite())
            {
                CommonUtils.EnsureFolderCreated(Configuration.GetConnectionString(IdentityConnectionName));

                // No migrations
                applicationDbContext.Database.EnsureCreated();
            }

            if (applicationDbContext.Database.IsSqlServer())
                // No migrations
                applicationDbContext.Database.EnsureCreated();


            if (applicationDbContext.Database.IsMySql() && applicationDbContext.Database.GetPendingMigrations().Any())
                // Use migrations
                applicationDbContext.Database.Migrate();

            using var registryDbContext = serviceScope.ServiceProvider.GetService<RegistryContext>();

            if (registryDbContext == null)
                throw new InvalidOperationException("Cannot get registry db context from service provider");

            if (registryDbContext.Database.IsSqlite())
            {
                CommonUtils.EnsureFolderCreated(Configuration.GetConnectionString(RegistryConnectionName));
                // No migrations
                registryDbContext.Database.EnsureCreated();
            }

            if (registryDbContext.Database.IsSqlServer())
                // No migrations
                registryDbContext.Database.EnsureCreated();


            if (registryDbContext.Database.IsMySql() && registryDbContext.Database.GetPendingMigrations().Any())
                // Use migrations
                registryDbContext.Database.Migrate();


            CreateInitialData(registryDbContext);
            CreateDefaultAdmin(registryDbContext, serviceScope.ServiceProvider).Wait();
        }

        private void RegisterHangfireProvider(IServiceCollection services, AppSettings appSettings)
        {
            switch (appSettings.HangfireProvider)
            {
                case HangfireProvider.InMemory:

                    services.AddHangfire(configuration => configuration
                        .SetDataCompatibilityLevel(CompatibilityLevel.Version_170)
                        .UseSimpleAssemblyNameTypeSerializer()
                        .UseRecommendedSerializerSettings()
                        .UseConsole()
                        .UseInMemoryStorage());

                    break;

                case HangfireProvider.Mysql:

                    services.AddHangfire(configuration => configuration
                        .SetDataCompatibilityLevel(CompatibilityLevel.Version_170)
                        .UseSimpleAssemblyNameTypeSerializer()
                        .UseRecommendedSerializerSettings()
                        .UseConsole()
                        .UseStorage(new MySqlStorage(Configuration.GetConnectionString(HangfireConnectionName),
                            new MySqlStorageOptions
                            {
                                TransactionIsolationLevel = IsolationLevel.ReadCommitted,
                                QueuePollInterval = TimeSpan.FromSeconds(15),
                                JobExpirationCheckInterval = TimeSpan.FromHours(1),
                                CountersAggregateInterval = TimeSpan.FromMinutes(5),
                                PrepareSchemaIfNecessary = true,
                                DashboardJobListLimit = 50000,
                                TransactionTimeout = TimeSpan.FromMinutes(10),
                                TablesPrefix = "hangfire"
                            })));

                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unsupported hangfire provider: '{appSettings.HangfireProvider}'");
            }

            // Add the processing server as IHostedService
            services.AddHangfireServer();
        }

        private void RegisterCacheProvider(IServiceCollection services, AppSettings appSettings)
        {
            if (appSettings.CacheProvider == null)
            {
                // Use memory caching
                services.AddDistributedMemoryCache();
                return;
            }

            switch (appSettings.CacheProvider.Type)
            {
                case CacheType.InMemory:

                    services.AddDistributedMemoryCache();

                    break;

                case CacheType.Redis:

                    var settings = appSettings.CacheProvider.Settings.ToObject<RedisProviderSettings>();

                    if (settings == null)
                        throw new ArgumentException("Invalid redis cache provider settings");

                    services.AddStackExchangeRedisCache(options =>
                    {
                        options.Configuration = settings.InstanceAddress;
                        options.InstanceName = settings.InstanceName;
                    });

                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unsupported caching provider: '{(int)appSettings.CacheProvider.Type}'");
            }
        }

        // private static void RegisterStorageProvider(IServiceCollection services, AppSettings appSettings)
        // {
        //     ICollection<ValidationResult> results;
        //
        //     switch (appSettings.StorageProvider.Type)
        //     {
        //         case StorageType.Physical:
        //
        //             var ps = appSettings.StorageProvider.Settings.ToObject<PhysicalProviderSettings>();
        //             if (ps == null)
        //                 throw new ArgumentException("Invalid physical storage provider settings");
        //
        //             if (!CommonUtils.Validate(ps, out results))
        //                 throw new ArgumentException("Invalid physical storage provider settings: " +
        //                                             results.ToErrorString());
        //
        //             Directory.CreateDirectory(ps.Path);
        //
        //             services.AddSingleton(new PhysicalObjectSystemSettings
        //             {
        //                 BasePath = ps.Path
        //             });
        //
        //             services.AddScoped<IObjectSystem, PhysicalObjectSystem>();
        //
        //             break;
        //
        //         case StorageType.S3:
        //
        //             var s3Settings = appSettings.StorageProvider.Settings.ToObject<S3StorageProviderSettings>();
        //
        //             if (s3Settings == null)
        //                 throw new ArgumentException("Invalid S3 storage provider settings");
        //
        //             if (!CommonUtils.Validate(s3Settings, out results))
        //                 throw new ArgumentException("Invalid S3 storage provider settings: " + results.ToErrorString());
        //
        //             services.AddSingleton(new S3ObjectSystemSettings
        //             {
        //                 Endpoint = s3Settings.Endpoint,
        //                 AccessKey = s3Settings.AccessKey,
        //                 SecretKey = s3Settings.SecretKey,
        //                 Region = s3Settings.Region,
        //                 SessionToken = s3Settings.SessionToken,
        //                 UseSsl = s3Settings.UseSsl ?? false,
        //                 AppName = s3Settings.AppName,
        //                 AppVersion = s3Settings.AppVersion,
        //                 BridgeUrl = s3Settings.BridgeUrl
        //             });
        //
        //             services.AddScoped<IObjectSystem, S3ObjectSystem>();
        //
        //             break;
        //
        //         case StorageType.CachedS3:
        //
        //             var cachedS3Settings = appSettings.StorageProvider.Settings
        //                 .ToObject<CachedS3StorageStorageProviderSettings>();
        //
        //             if (cachedS3Settings == null)
        //                 throw new ArgumentException("Invalid S3 storage provider settings");
        //
        //             if (!CommonUtils.Validate(cachedS3Settings, out results))
        //                 throw new ArgumentException("Invalid S3 storage provider settings: " + results.ToErrorString());
        //
        //             services.AddSingleton(new CachedS3ObjectSystemSettings
        //             {
        //                 Endpoint = cachedS3Settings.Endpoint,
        //                 AccessKey = cachedS3Settings.AccessKey,
        //                 SecretKey = cachedS3Settings.SecretKey,
        //                 Region = cachedS3Settings.Region,
        //                 SessionToken = cachedS3Settings.SessionToken,
        //                 UseSsl = cachedS3Settings.UseSsl ?? false,
        //                 AppName = cachedS3Settings.AppName,
        //                 AppVersion = cachedS3Settings.AppVersion,
        //                 BridgeUrl = cachedS3Settings.BridgeUrl,
        //                 CacheExpiration = cachedS3Settings.CacheExpiration,
        //                 CachePath = cachedS3Settings.CachePath,
        //                 MaxSize = cachedS3Settings.MaxSize
        //             });
        //
        //             services.AddSingleton<IObjectSystem, CachedS3ObjectSystem>();
        //
        //             break;
        //
        //         default:
        //             throw new InvalidOperationException(
        //                 $"Unsupported storage provider: '{(int)appSettings.StorageProvider.Type}'");
        //     }
        // }

        private void ConfigureDbProvider<T>(IServiceCollection services, DbProvider provider,
            string connectionStringName) where T : DbContext
        {
            var connectionString = Configuration.GetConnectionString(connectionStringName);

            services.AddDbContext<T>(options =>
                _ = provider switch
                {
                    DbProvider.Sqlite => options.UseSqlite(connectionString),
                    DbProvider.Mysql => options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString),
                        builder => builder.EnableRetryOnFailure()),
                    DbProvider.Mssql => options.UseSqlServer(connectionString),
                    _ => throw new ArgumentOutOfRangeException(nameof(provider), $"Unrecognised provider: '{provider}'")
                });
        }

        private void CreateInitialData(RegistryContext context)
        {
            // If no organizations in database, let's create the public one
            if (!context.Organizations.Any())
            {
                var entity = new Organization
                {
                    Slug = MagicStrings.PublicOrganizationSlug,
                    Name = MagicStrings.PublicOrganizationSlug.ToPascalCase(false, CultureInfo.InvariantCulture),
                    CreationDate = DateTime.Now,
                    Description = "Organization",
                    IsPublic = true,
                    // NOTE: Maybe this is a good idea to flag this org as "system"
                    OwnerId = null
                };
                var ds = new Dataset
                {
                    Slug = MagicStrings.DefaultDatasetSlug,
                    Name = MagicStrings.DefaultDatasetSlug.ToPascalCase(false, CultureInfo.InvariantCulture),
                    //IsPublic = true,
                    CreationDate = DateTime.Now,
                    //LastUpdate = DateTime.Now,
                    InternalRef = Guid.NewGuid()
                };
                entity.Datasets = new List<Dataset> { ds };

                context.Organizations.Add(entity);
                context.SaveChanges();
            }
        }

        private async Task CreateDefaultAdmin(RegistryContext context, IServiceProvider provider)
        {
            var usersManager = provider.GetService<UserManager<User>>();
            var roleManager = provider.GetService<RoleManager<IdentityRole>>();
            var appSettings = provider.GetService<IOptions<AppSettings>>();

            if (usersManager == null)
                throw new InvalidOperationException("Cannot get users manager from service provider");

            if (roleManager == null)
                throw new InvalidOperationException("Cannot get role manager from service provider");

            if (appSettings == null)
                throw new InvalidOperationException("Cannot get app settings from service provider");

            // If no users in database, let's create the default admin
            if (!usersManager.Users.Any())
            {
                // first we create Admin role  
                var role = new IdentityRole { Name = ApplicationDbContext.AdminRoleName };
                var r = await roleManager.CreateAsync(role);

                if (!r.Succeeded)
                    throw new InvalidOperationException("Cannot create admin role: " + r?.Errors.ToErrorString());

                var defaultAdmin = appSettings.Value.DefaultAdmin;
                var user = new User
                {
                    Email = defaultAdmin.Email,
                    UserName = defaultAdmin.UserName
                };

                var usrRes = await usersManager.CreateAsync(user, defaultAdmin.Password);
                if (!usrRes.Succeeded)
                    throw new InvalidOperationException(
                        "Cannot create default admin: " + usrRes.Errors?.ToErrorString());

                var res = await usersManager.AddToRoleAsync(user, ApplicationDbContext.AdminRoleName);
                if (!res.Succeeded)
                    throw new InvalidOperationException(
                        "Cannot add admin to admin role: " + res.Errors?.ToErrorString());

                var entity = new Organization
                {
                    Slug = defaultAdmin.UserName.ToSlug(),
                    Name = defaultAdmin.UserName + " organization",
                    CreationDate = DateTime.Now,
                    Description = null,
                    IsPublic = true,
                    // NOTE: Maybe this is a good idea to flag this org as "system"
                    OwnerId = user.Id
                };

                await context.Organizations.AddAsync(entity);
                await context.SaveChangesAsync();
            }
        }
    }
}