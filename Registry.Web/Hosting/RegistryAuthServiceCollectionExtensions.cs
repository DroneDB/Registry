#nullable enable
using System;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Registry.Web.Filters;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Web.Middlewares;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Registry.Web.Utilities.Auth;

namespace Registry.Web.Hosting;

/// <summary>Authentication and authorization of the web host.</summary>
public static class RegistryAuthServiceCollectionExtensions
{
    /// <summary>
    /// The identity database and store (one Identity builder; the sign-in manager for local accounts
    /// only), the login manager of the configured provider (LDAP, external or local), JWT bearer
    /// authentication, the authorization policies and handlers (RQ-06), the token manager and the auth
    /// middlewares, the cached auth facts, the dataset access resolver and the membership service.
    /// </summary>
    public static IServiceCollection AddRegistryAuth(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var store = configuration.GetRegistryOptions<DataStoreOptions>();
        var identity = configuration.GetRegistryOptions<IdentityProviderOptions>();

        services.AddDbContextWithProvider<ApplicationDbContext>(configuration, store.AuthProvider,
            MagicStrings.IdentityConnectionName, "Identity");

        var identityBuilder = services.AddIdentityCore<User>()
            .AddUserManager<RegistryUserManager>()
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddDefaultTokenProviders();

        if (identity.LdapEnabled)
            services.AddScoped<ILoginManager, LdapLoginManager>();
        else if (identity.ExternalAuthEnabled)
            services.AddScoped<ILoginManager, RemoteLoginManager>();
        else
        {
            identityBuilder.AddSignInManager();
            services.AddScoped<ILoginManager, LocalLoginManager>();
        }

        services.AddOptions<IdentityOptions>().Configure<IOptions<IdentityProviderOptions>>((options, provider) =>
            ConfigureIdentity(options, provider.Value.PasswordPolicy));

        services.AddAuthentication(auth =>
            {
                auth.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                auth.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<AuthTokenOptions>>((jwt, tokens) =>
            {
                jwt.RequireHttpsMetadata = false;
                jwt.SaveToken = true;
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(tokens.Value.Secret!)),
                    ValidateIssuer = false,
                    ValidateAudience = false
                };
            });

        services.AddAuthorization(options => options.AddRegistryPolicies());
        services.AddScoped<IAuthorizationHandler, AdminAuthorizationHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, RegistryAuthorizationResultHandler>();

        services.AddTransient<TokenManagerMiddleware>();
        services.AddTransient<JwtInCookieMiddleware>();
        services.AddTransient<UserEnrichmentMiddleware>();
        services.AddTransient<ITokenManager, TokenManager>();
        services.AddScoped<BasicAuthFilter>();

        services.AddSingleton<UserAuthSnapshotCache>();
        services.AddScoped<IUserAuthSnapshotProvider, UserAuthSnapshotProvider>();
        services.AddScoped<IAuthManager, AuthManager>();
        // A request queues builds as its signed-in user (the job hosts' requester is the service)
        services.Replace(ServiceDescriptor.Scoped<Services.Builds.IBuildRequester, Services.Builds.CurrentUserBuildRequester>());
        services.AddScoped<IDatasetAccessResolver, DatasetAccessResolver>();
        services.AddScoped<IOrganizationMembershipService, OrganizationMembershipService>();
        services.AddSingleton<IPasswordPolicyValidator, PasswordPolicyValidator>();

        return services;
    }

    /// <summary>Identity's password, lockout and user name rules (<paramref name="policy"/> null: no password requirements).</summary>
    internal static void ConfigureIdentity(IdentityOptions options, PasswordPolicy? policy)
    {
        options.Password.RequireDigit = policy?.RequireDigit ?? false;
        options.Password.RequireLowercase = policy?.RequireLowercase ?? false;
        options.Password.RequireNonAlphanumeric = policy?.RequireNonAlphanumeric ?? false;
        options.Password.RequireUppercase = policy?.RequireUppercase ?? false;
        options.Password.RequiredLength = policy?.MinLength ?? 1;
        options.Password.RequiredUniqueChars = 0;

        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.AllowedForNewUsers = true;

        options.User.AllowedUserNameCharacters =
            "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._@+";
        options.User.RequireUniqueEmail = false;
    }
}
