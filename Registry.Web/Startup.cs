using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Authentication.Cookies;
using Registry.Web.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.StaticFiles.Infrastructure;
using Microsoft.CodeAnalysis.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Registry.Adapters.DroneDB;
using Registry.Adapters.ObjectSystem;
using Registry.Adapters.ObjectSystem.Model;
using Registry.Common;
using Registry.Ports.DroneDB;
using Registry.Ports.ObjectSystem;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.HealthChecks;
using Registry.Web.Middlewares;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using RestSharp.Extensions;

namespace Registry.Web
{
    public class Startup
    {
        private const string IdentityConnectionName = "IdentityConnection";
        private const string RegistryConnectionName = "RegistryConnection";

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
                        Name = "Use under Business Source License",
                        Url = new Uri("https://github.com/DroneDB/Registry/blob/master/LICENSE.md"),
                    }
                });
                c.DocumentFilter<BasePathDocumentFilter>();
            });

            services.AddMvcCore().AddNewtonsoftJson();

            services.AddSpaStaticFiles(config =>
            {
                config.RootPath = "ClientApp/build";
            });

            // Let's use a strongly typed class for settings
            var appSettingsSection = Configuration.GetSection("AppSettings");
            services.Configure<AppSettings>(appSettingsSection);

            var appSettings = appSettingsSection.Get<AppSettings>();

            ConfigureDbProvider<ApplicationDbContext>(services, appSettings.AuthProvider, IdentityConnectionName);

            if (!string.IsNullOrWhiteSpace(appSettings.ExternalAuthUrl))
            {
                services.AddIdentityCore<User>()
                    .AddRoles<IdentityRole>()
                    .AddEntityFrameworkStores<ApplicationDbContext>()
                    .AddSignInManager<ExternalSignInManager>();
            }
            else
            {
                services.AddIdentityCore<User>()
                    .AddRoles<IdentityRole>()
                    .AddEntityFrameworkStores<ApplicationDbContext>()
                    .AddSignInManager();
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

            RegisterCacheProvider(services, appSettings);

            services.AddHealthChecks()
                .AddCheck<CacheHealthCheck>("Cache health check", null, new[] { "service" })
                .AddCheck<DdbHealthCheck>("DroneDB health check", null, new[] { "service" })
                .AddCheck<UserManagerHealthCheck>("User manager health check", null, new[] { "database" })
                .AddDbContextCheck<RegistryContext>("Registry database health check", null, new[] { "database" })
                .AddDbContextCheck<ApplicationDbContext>("Registry identity database health check", null,
                    new[] { "database" })
                .AddCheck<ObjectSystemHealthCheck>("Object system health check", null, new[] { "storage" })
                .AddDiskSpaceHealthCheck(appSettings.UploadPath, "Upload path space health check", null,
                    new[] { "storage" })
                .AddDiskSpaceHealthCheck(appSettings.DdbStoragePath, "Ddb storage path space health check", null,
                    new[] { "storage" });

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

            services.AddScoped<IChunkedUploadManager, ChunkedUploadManager>();
            services.AddScoped<IUsersManager, UsersManager>();
            services.AddScoped<IOrganizationsManager, OrganizationsManager>();
            services.AddScoped<IDatasetsManager, DatasetsManager>();
            services.AddScoped<IObjectsManager, ObjectsManager>();
            services.AddScoped<IShareManager, ShareManager>();
            services.AddScoped<IPushManager, PushManager>();
            services.AddScoped<IDdbManager, DdbManager>();
            services.AddScoped<ISystemManager, SystemManager>();

            services.AddSingleton<IPasswordHasher, PasswordHasher>();
            services.AddSingleton<IBatchTokenGenerator, BatchTokenGenerator>();
            services.AddSingleton<INameGenerator, NameGenerator>();
            services.AddSingleton<ICacheManager, CacheManager>();

            RegisterStorageProvider(services, appSettings);

            services.AddResponseCompression();

            services.Configure<FormOptions>(options =>
            {
                // See https://docs.microsoft.com/it-it/aspnet/core/fundamentals/servers/kestrel?view=aspnetcore-3.1#maximum-client-connections
                // We could put this in config "Kestrel->Limits" section
                options.MultipartBodyLengthLimit = appSettings.MaxRequestBodySize;
            });

            services.AddHttpContextAccessor();

            // If using Kestrel:
            services.Configure<KestrelServerOptions>(options =>
            {
                options.AllowSynchronousIO = true;
            });

            // If using IIS:
            services.Configure<IISServerOptions>(options =>
            {
                options.AllowSynchronousIO = true;
            });

            // TODO: Enable when needed. Should check return object structure
            // services.AddOData();

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
            app.UseSpaStaticFiles();

            app.UseSwagger();

            app.UseSwaggerUI(c =>
            {
                c.SwaggerEndpoint("/swagger/v1/swagger.json", "Registry API");
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

            app.UseMiddleware<TokenManagerMiddleware>();

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

                endpoints.MapGet("/version", async context =>
                {
                    await context.Response.WriteAsync(Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "undefined");
                });


                // TODO: Enable when needed
                // endpoints.MapODataRoute("odata", "odata", GetEdmModel());
            });

            app.UseSpa(spa =>
            {
                // To learn more about options for serving an Angular SPA from ASP.NET Core,
                // see https://go.microsoft.com/fwlink/?linkid=864501

                spa.Options.SourcePath = "ClientApp";

                if (env.IsDevelopment())
                {

                }
            });

            SetupDatabase(app);
            
        }
        
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

        private static void RegisterStorageProvider(IServiceCollection services, AppSettings appSettings)
        {
            switch (appSettings.StorageProvider.Type)
            {
                case StorageType.Physical:

                    var pySettings = appSettings.StorageProvider.Settings.ToObject<PhysicalProviderSettings>();
                    if (pySettings == null)
                        throw new ArgumentException("Invalid physical storage provider settings");

                    var basePath = pySettings.Path;

                    Directory.CreateDirectory(basePath);

                    services.AddScoped<IObjectSystem>(provider => new PhysicalObjectSystem(basePath));

                    break;

                case StorageType.S3:

                    var s3Settings = appSettings.StorageProvider.Settings.ToObject<S3StorageProviderSettings>();

                    if (s3Settings == null)
                        throw new ArgumentException("Invalid S3 storage provider settings");

                    services.AddSingleton(new S3ObjectSystemSettings
                    {
                        Endpoint = s3Settings.Endpoint,
                        AccessKey = s3Settings.AccessKey,
                        SecretKey = s3Settings.SecretKey,
                        Region = s3Settings.Region,
                        SessionToken = s3Settings.SessionToken,
                        UseSsl = s3Settings.UseSsl ?? false,
                        AppName = s3Settings.AppName,
                        AppVersion = s3Settings.AppVersion
                    });

                    services.AddScoped<IObjectSystem, S3ObjectSystem>();

                    break;

                case StorageType.CachedS3:

                    var cachedS3Settings = appSettings.StorageProvider.Settings.ToObject<CachedS3StorageStorageProviderSettings>();

                    if (cachedS3Settings == null)
                        throw new ArgumentException("Invalid S3 storage provider settings");

                    services.AddSingleton(new CachedS3ObjectSystemSettings
                    {
                        Endpoint = cachedS3Settings.Endpoint,
                        AccessKey = cachedS3Settings.AccessKey,
                        SecretKey = cachedS3Settings.SecretKey,
                        Region = cachedS3Settings.Region,
                        SessionToken = cachedS3Settings.SessionToken,
                        UseSsl = cachedS3Settings.UseSsl ?? false,
                        AppName = cachedS3Settings.AppName,
                        AppVersion = cachedS3Settings.AppVersion,
                        CacheExpiration = cachedS3Settings.CacheExpiration,
                        CachePath = cachedS3Settings.CachePath,
                        MaxSize = cachedS3Settings.MaxSize
                    });

                    services.AddScoped<IObjectSystem, CachedS3ObjectSystem>();


                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unsupported storage provider: '{(int)appSettings.StorageProvider.Type}'");
            }
        }

        private void ConfigureDbProvider<T>(IServiceCollection services, DbProvider provider, string connectionStringName) where T : DbContext
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
                    Description = "Default dataset",
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
                    throw new InvalidOperationException("Cannot create default admin: " + usrRes.Errors?.ToErrorString());

                var res = await usersManager.AddToRoleAsync(user, ApplicationDbContext.AdminRoleName);
                if (!res.Succeeded)
                    throw new InvalidOperationException("Cannot add admin to admin role: " + res.Errors?.ToErrorString());

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
