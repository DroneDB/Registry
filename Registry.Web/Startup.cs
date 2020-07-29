using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNet.OData.Builder;
using Microsoft.AspNet.OData.Extensions;
using Registry.Web.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.CodeAnalysis.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OData.Edm;
using Registry.Adapters.ObjectSystem;
using Registry.Common;
using Registry.Ports.ObjectSystem;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;

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

            // Let's use a strongly typed class for settings
            var appSettingsSection = Configuration.GetSection("AppSettings");
            services.Configure<AppSettings>(appSettingsSection);

            var appSettings = appSettingsSection.Get<AppSettings>();

            ConfigureDbProvider<ApplicationDbContext>(services, appSettings.AuthProvider, IdentityConnectionName);

            services.AddIdentityCore<User>()
                .AddRoles<IdentityRole>()
                .AddEntityFrameworkStores<ApplicationDbContext>()
                .AddSignInManager();

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

            services.AddTransient<TokenManagerMiddleware>();
            services.AddTransient<ITokenManager, TokenManager>();
            services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
            services.AddMemoryCache();

            services.AddSingleton<IUtils, WebUtils>();
            services.AddScoped<IAuthManager, AuthManager>();
            services.AddScoped<IOrganizationsManager, OrganizationsManager>();
            services.AddScoped<IDatasetsManager, DatasetsManager>();
            services.AddScoped<IObjectsManager, ObjectsManager>();

            switch (appSettings.StorageProvider.Type)
            {
                case StorageType.Physical:
                    var basePath = appSettings.StorageProvider.Settings["path"];

                    if (!Directory.Exists(basePath))
                        Directory.CreateDirectory(basePath);

                    services.AddScoped<IObjectSystem>(provider => new PhysicalObjectSystem(basePath));
                    break;
                case StorageType.S3:

                    // TODO: Need test and maybe validation. Better to create a S3ObjectSystemConfig with this fields
                    var endpoint = appSettings.StorageProvider.Settings.SafeGetValue("endpoint");
                    var accessKey = appSettings.StorageProvider.Settings.SafeGetValue("accessKey");
                    var secretKey = appSettings.StorageProvider.Settings.SafeGetValue("secretKey");
                    var region = appSettings.StorageProvider.Settings.SafeGetValue("region");
                    var sessionToken = appSettings.StorageProvider.Settings.SafeGetValue("sessionToken");
                    var useSsl = appSettings.StorageProvider.Settings.SafeGetValue("useSsl");

                    if (!bool.TryParse(useSsl, out var tmp))
                    {
                        tmp = false;
                    }

                    var appName = appSettings.StorageProvider.Settings.SafeGetValue("appName");
                    var appVersion = appSettings.StorageProvider.Settings.SafeGetValue("appVersion");

                    services.AddScoped<IObjectSystem, S3ObjectSystem>(provider => new S3ObjectSystem(endpoint, accessKey,
                        secretKey, region, sessionToken, tmp, appName, appVersion));
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported storage provider: '{(int)appSettings.StorageProvider.Type}'");
            }

            // TODO: Enable when needed. Should check return object structure
            // services.AddOData();

        }

        private void ConfigureDbProvider<T>(IServiceCollection services, DbProvider provider, string connectionStringName) where T : DbContext
        {
            switch (provider)
            {
                case DbProvider.Sqlite:

                    services.AddDbContext<T>(options =>
                        options.UseSqlite(
                            Configuration.GetConnectionString(connectionStringName)));

                    break;

                case DbProvider.Mysql:

                    services.AddDbContext<T>(options =>
                        options.UseMySql(
                            Configuration.GetConnectionString(connectionStringName)));

                    break;

                case DbProvider.Mssql:

                    services.AddDbContext<T>(options =>
                        options.UseSqlServer(
                            Configuration.GetConnectionString(connectionStringName)));

                    break;

                default:
                    throw new ArgumentOutOfRangeException($"Unrecognised provider: '{provider}'");
            }
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            // We are permissive now
            app.UseCors(cors => cors
                .AllowAnyOrigin()
                .AllowAnyMethod()
                .AllowAnyHeader());

            app.UseRouting();

            app.UseAuthentication();
            app.UseAuthorization();

            app.UseMiddleware<TokenManagerMiddleware>();

            app.UseDefaultFiles();
            app.UseStaticFiles();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
                // TODO: Enable when needed
                // endpoints.MapODataRoute("odata", "odata", GetEdmModel());
            });

            SetupDatabase(app);

        }
        private IEdmModel GetEdmModel()
        {
            var odataBuilder = new ODataConventionModelBuilder();
            odataBuilder.EntitySet<OrganizationDto>("Organizations");

            return odataBuilder.GetEdmModel();
        }

        // NOTE: Maybe put all this login as stated in https://stackoverflow.com/a/55707949
        private void SetupDatabase(IApplicationBuilder app)
        {
            using var serviceScope = app.ApplicationServices
                .GetRequiredService<IServiceScopeFactory>()
                .CreateScope();
            using var applicationDbContext = serviceScope.ServiceProvider.GetService<ApplicationDbContext>();

            if (applicationDbContext.Database.IsSqlite())
                CommonUtils.EnsureFolderCreated(Configuration.GetConnectionString(IdentityConnectionName));
            
            applicationDbContext.Database.EnsureCreated();

            CreateDefaultAdmin(serviceScope.ServiceProvider).Wait();

            using var registryDbContext = serviceScope.ServiceProvider.GetService<RegistryContext>();

            if (registryDbContext.Database.IsSqlite())
                CommonUtils.EnsureFolderCreated(Configuration.GetConnectionString(RegistryConnectionName));
            
            registryDbContext.Database.EnsureCreated();

            CreateInitialData(registryDbContext);

        }

        private void CreateInitialData(RegistryContext context)
        {
            // If no organizations in database, let's create the public one
            if (!context.Organizations.Any())
            {

                var entity = new Organization
                {
                    Id = MagicStrings.PublicOrganizationId,
                    Name = "Public",
                    CreationDate = DateTime.Now,
                    Description = "Public organization",
                    IsPublic = true,
                    // NOTE: Maybe this is a good idea to flag this org as "system"
                    OwnerId = null
                };
                var ds = new Dataset
                {
                    Slug = MagicStrings.DefaultDatasetSlug,
                    Name = "Default",
                    Description = "Default dataset",
                    IsPublic = true,
                    CreationDate = DateTime.Now,
                    LastEdit = DateTime.Now
                };
                entity.Datasets = new List<Dataset> { ds };

                context.Organizations.Add(entity);
                context.SaveChanges();
            }
        }

        private async Task CreateDefaultAdmin(IServiceProvider provider)
        {

            var usersManager = provider.GetService<UserManager<User>>();
            var roleManager = provider.GetService<RoleManager<IdentityRole>>();
            var appSettings = provider.GetService<IOptions<AppSettings>>();

            // If no users in database, let's create the default admin
            if (!usersManager.Users.Any())
            {
                // first we create Admin role  
                var role = new IdentityRole { Name = ApplicationDbContext.AdminRoleName };
                await roleManager.CreateAsync(role);

                var defaultAdmin = appSettings.Value.DefaultAdmin;
                var user = new User
                {
                    Email = defaultAdmin.Email,
                    UserName = defaultAdmin.UserName
                };

                var usrRes = await usersManager.CreateAsync(user, defaultAdmin.Password);
                if (usrRes.Succeeded)
                {
                    var res = await usersManager.AddToRoleAsync(user, ApplicationDbContext.AdminRoleName);
                }
            }
        }


    }
}
