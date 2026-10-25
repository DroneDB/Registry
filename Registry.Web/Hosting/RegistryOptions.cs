#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Registry.Web.Models.Configuration;
using Registry.Web.Utilities;

namespace Registry.Web.Hosting;

/// <summary>
/// The options of the Registry: one table of the focused classes and the section each binds,
/// one registration path (bind, defaults, validator, <c>ValidateOnStart</c>), and the registration-time
/// read of a class for the composition decisions that need a value before the container exists.
/// </summary>
public static class RegistryOptions
{
    /// <summary>The configuration section the Registry settings live in (<c>appsettings.json</c>, <c>AppSettings__…</c> variables).</summary>
    public const string SectionName = "AppSettings";

    /// <summary>
    /// The section each focused class binds. The keys are the ones the Registry always read: the flat
    /// classes split the <c>AppSettings</c> section by feature, the nested ones keep their subsection.
    /// </summary>
    public static readonly IReadOnlyDictionary<Type, string> Sections = new Dictionary<Type, string>
    {
        [typeof(AuthTokenOptions)] = SectionName,
        [typeof(IdentityProviderOptions)] = SectionName,
        [typeof(AccountOptions)] = SectionName,
        [typeof(DataStoreOptions)] = SectionName,
        [typeof(StorageOptions)] = SectionName,
        [typeof(CachingOptions)] = SectionName,
        [typeof(HttpHostOptions)] = SectionName,
        [typeof(JobScheduleOptions)] = SectionName,
        [typeof(ThumbnailOptions)] = SectionName,
        [typeof(DownloadOptions)] = SectionName,
        [typeof(ShareOptions)] = SectionName,
        [typeof(HostResourceOptions)] = SectionName,
        [typeof(ImportSettings)] = Nested("Import"),
        [typeof(ProcessingPlatformSettings)] = Nested("ProcessingPlatform"),
        [typeof(UploadSettings)] = Nested("Uploads"),
        [typeof(IndexQueueSettings)] = Nested("IndexQueue"),
        [typeof(ReconciliationSettings)] = Nested("Reconciliation"),
        [typeof(HubOptions)] = Nested("HubOptions")
    };

    private static string Nested(string name) => SectionName + ":" + name;

    /// <summary>
    /// Registers every options class of <see cref="Sections"/> and <see cref="DdbRuntimeOptions"/>, each with
    /// its validator and <c>ValidateOnStart</c>, plus the rules of <paramref name="role"/>. Idempotent: the modules of a host may each call it.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The host configuration the options bind.</param>
    /// <param name="role">The instance type of the host (adds the rules that apply to it only).</param>
    public static IServiceCollection AddRegistryOptions(this IServiceCollection services, IConfiguration configuration,
        InstanceType role)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (services.Any(d => d.ServiceType == typeof(RegistryOptionsMarker)))
            return services;
        services.AddSingleton<RegistryOptionsMarker>();

        Add<AuthTokenOptions, AuthTokenOptionsValidator>(services, configuration);
        Add<IdentityProviderOptions, IdentityProviderOptionsValidator>(services, configuration);
        Add<AccountOptions>(services, configuration);
        Add<DataStoreOptions>(services, configuration);
        Add<StorageOptions, StorageOptionsValidator>(services, configuration);
        Add<CachingOptions, CachingOptionsValidator>(services, configuration);
        Add<HttpHostOptions, HttpHostOptionsValidator>(services, configuration);
        Add<JobScheduleOptions>(services, configuration);
        Add<ThumbnailOptions>(services, configuration);
        Add<DownloadOptions>(services, configuration);
        Add<ShareOptions, ShareOptionsValidator>(services, configuration);
        Add<HostResourceOptions, HostResourceOptionsValidator>(services, configuration);
        Add<ImportSettings>(services, configuration);
        Add<ProcessingPlatformSettings>(services, configuration);
        Add<UploadSettings>(services, configuration);
        Add<IndexQueueSettings>(services, configuration);
        Add<ReconciliationSettings>(services, configuration);
        Add<HubOptions>(services, configuration);

        // Rules of one role only
        switch (role)
        {
            case InstanceType.ThumbnailGenerator:
                Validate<ThumbnailOptions, ThumbnailGeneratorTokenValidator>(services);
                break;
            case InstanceType.ProcessingNode:
                Validate<DataStoreOptions, ProcessingNodeDataStoreValidator>(services);
                break;
        }

        // libddb's process settings: the cache under the temp folder by default
        services.AddOptions<DdbRuntimeOptions>()
            .Bind(configuration.GetSection(DdbRuntimeOptions.SectionName))
            .PostConfigure<IOptions<StorageOptions>>((options, storage) =>
                options.CacheDir = DdbRuntimeServiceCollectionExtensions.ResolveCacheDir(options.CacheDir, storage.Value.TempPath))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // What this process may use of the machine, computed once from the options above
        services.TryAddSingleton(sp => HostResourceBudget.Create(role, HostFacts.Current(),
            sp.GetRequiredService<IOptions<HostResourceOptions>>().Value,
            sp.GetRequiredService<IOptions<DdbRuntimeOptions>>().Value, Environment.GetEnvironmentVariable));

        return services;
    }

    /// <summary>The instance type of the host (configuration key <see cref="Program.InstanceTypeKey"/>, set by <c>RegistryHost</c>).</summary>
    public static InstanceType GetInstanceType(this IConfiguration configuration) =>
        configuration.GetValue(Program.InstanceTypeKey, InstanceType.Default);

    /// <summary>
    /// The options class <typeparamref name="T"/> read from <paramref name="configuration"/> with its
    /// defaults applied, as the container will bind it: for the composition decisions made before the
    /// container exists (which database provider, which login manager, which thumbnail generator).
    /// </summary>
    public static T GetRegistryOptions<T>(this IConfiguration configuration) where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var options = configuration.GetSection(SectionOf<T>()).Get<T>() ?? new T();
        (options as IHasConfigurationDefaults)?.ApplyDefaults();
        return options;
    }

    /// <summary>The section of <typeparamref name="T"/> (see <see cref="Sections"/>).</summary>
    public static string SectionOf<T>() =>
        Sections.TryGetValue(typeof(T), out var section)
            ? section
            : throw new InvalidOperationException($"{typeof(T).Name} is not a Registry options class");

    /// <summary>
    /// Validates the options of <paramref name="configuration"/> for <paramref name="role"/> with the
    /// rules the host runs at start, without building a host: the errors, empty when it is valid.
    /// </summary>
    public static IReadOnlyList<string> Validate(IConfiguration configuration, InstanceType role)
    {
        var services = new ServiceCollection();
        services.AddRegistryOptions(configuration, role);

        using var provider = services.BuildServiceProvider();
        try
        {
            provider.GetRequiredService<IStartupValidator>().Validate();
            return [];
        }
        catch (OptionsValidationException ex)
        {
            return ex.Failures.Distinct().ToList();
        }
        catch (AggregateException ex)
        {
            // An options class that reads another (DdbRuntimeOptions reads StorageOptions) repeats its failure
            return ex.Flatten().InnerExceptions
                .SelectMany(inner => inner is OptionsValidationException validation ? validation.Failures : [inner.Message])
                .Distinct()
                .ToList();
        }
    }

    private static OptionsBuilder<T> Add<T>(IServiceCollection services, IConfiguration configuration) where T : class
    {
        var builder = services.AddOptions<T>().Bind(configuration.GetSection(SectionOf<T>()));

        if (typeof(IHasConfigurationDefaults).IsAssignableFrom(typeof(T)))
            builder.PostConfigure(options => ((IHasConfigurationDefaults)options).ApplyDefaults());

        return builder.ValidateOnStart();
    }

    private static void Add<T, TValidator>(IServiceCollection services, IConfiguration configuration)
        where T : class where TValidator : class, IValidateOptions<T>
    {
        Add<T>(services, configuration);
        Validate<T, TValidator>(services);
    }

    private static void Validate<T, TValidator>(IServiceCollection services)
        where T : class where TValidator : class, IValidateOptions<T> =>
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<T>, TValidator>());

    /// <summary>Marks a container whose options are registered.</summary>
    private sealed class RegistryOptionsMarker;
}
