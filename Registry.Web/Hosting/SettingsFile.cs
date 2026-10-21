#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Registry.Common;
using Registry.Web.Services.Adapters;
using Registry.Web.Models.Configuration;
using Registry.Web.Utilities;

namespace Registry.Web.Hosting;

/// <summary>
/// The storage folder's <c>appsettings.json</c> (RQ-07). A first start creates it from the embedded
/// defaults; every start adds the secrets that are missing (<see cref="GeneratedSecrets"/>) and changes
/// nothing else, so a normal start does not write the file. Writes are atomic
/// (<see cref="AtomicFile"/>). <c>--check</c> validates the same content the host would read, with the
/// same rules, without writing.
/// </summary>
public static class SettingsFile
{
    /// <summary>Length of a generated secret.</summary>
    public const int SecretLength = 64;

    /// <summary>
    /// The <c>AppSettings</c> keys generated when empty: the JWT signing secret, and the token shared by
    /// the web server and the thumbnail generator (both read the same file).
    /// </summary>
    public static readonly IReadOnlyList<string> GeneratedSecrets =
        [nameof(AuthTokenOptions.Secret), nameof(ThumbnailOptions.ThumbnailGeneratorToken)];

    /// <summary>The settings file of <paramref name="storageFolder"/>.</summary>
    public static string PathIn(string storageFolder) =>
        Path.Combine(Path.GetFullPath(storageFolder), MagicStrings.AppSettingsFileName);

    /// <summary>The embedded <c>appsettings-default.json</c> (what a first start writes).</summary>
    public static string DefaultContent()
    {
        using var stream = new EmbeddedResourceQuery().Read(Assembly.GetExecutingAssembly(),
                               MagicStrings.AppSettingsDefaultFileName) ??
                           throw new InvalidOperationException($"{MagicStrings.AppSettingsDefaultFileName} is not embedded");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Creates the file from the defaults when it does not exist, then adds the missing secrets.
    /// </summary>
    /// <returns>What was done, for the log.</returns>
    /// <exception cref="JsonException">The file is not valid JSON.</exception>
    public static SettingsFileChanges Prepare(string storageFolder)
    {
        var path = PathIn(storageFolder);
        var created = false;

        if (!File.Exists(path))
        {
            AtomicFile.WriteAllText(path, DefaultContent());
            created = true;
        }

        var settings = Read(path);
        var generated = AddMissingSecrets(settings);
        var atomic = true;

        if (generated.Count > 0)
            atomic = AtomicFile.WriteAllText(path, settings.ToString(Formatting.Indented));

        return new SettingsFileChanges(path, created, generated, atomic);
    }

    /// <summary>The file's JSON.</summary>
    /// <exception cref="JsonException">The file is not valid JSON or not an object.</exception>
    public static JObject Read(string path) =>
        JsonConvert.DeserializeObject<JObject>(File.ReadAllText(path),
            new JsonSerializerSettings { DateParseHandling = DateParseHandling.None }) ??
        throw new JsonSerializationException($"{path} is empty");

    /// <summary>
    /// Sets each of <see cref="GeneratedSecrets"/> that is missing or blank in the <c>AppSettings</c>
    /// object of <paramref name="settings"/> to a new random value; leaves everything else as it is.
    /// </summary>
    /// <returns>The keys that were generated.</returns>
    public static IReadOnlyList<string> AddMissingSecrets(JObject settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings[AppSettings.SectionName] is not JObject section)
        {
            section = new JObject();
            settings[AppSettings.SectionName] = section;
        }

        var generated = new List<string>();
        foreach (var key in GeneratedSecrets)
        {
            if (!string.IsNullOrWhiteSpace(section.Value<string>(key)))
                continue;

            section[key] = CommonUtils.RandomString(SecretLength);
            generated.Add(key);
        }

        return generated;
    }

    /// <summary>
    /// The configuration a host of <paramref name="storageFolder"/> would read for the options: the file
    /// (with the secrets a start would generate, in memory only) and the environment variables.
    /// </summary>
    /// <exception cref="FileNotFoundException">The folder has no settings file.</exception>
    /// <exception cref="JsonException">The file is not valid JSON.</exception>
    public static IConfiguration LoadConfiguration(string storageFolder)
    {
        var path = PathIn(storageFolder);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"'{path}' not found: run the Registry once on this storage folder to create it", path);

        var settings = Read(path);
        AddMissingSecrets(settings);

        return new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(settings.ToString(Formatting.None))))
            .AddEnvironmentVariables()
            .Build();
    }
}

/// <summary>What <see cref="SettingsFile.Prepare"/> did.</summary>
/// <param name="Path">The settings file.</param>
/// <param name="Created">True when it was created from the defaults.</param>
/// <param name="GeneratedSecrets">The secrets that were generated and written.</param>
/// <param name="Atomic">False when the file is a mount point and was written in place.</param>
public sealed record SettingsFileChanges(string Path, bool Created, IReadOnlyList<string> GeneratedSecrets, bool Atomic);
