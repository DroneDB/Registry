#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using Serilog;

namespace Registry.Web.Test.Support;

/// <summary>
/// Runs <see cref="Program"/> command line paths in-process: a temp storage folder, the shipped default
/// settings, captured stdout/stderr, and the process-wide state the command line touches (current
/// directory, static Serilog logger) restored afterwards.
/// </summary>
public sealed class CommandLineHarness : IDisposable
{
    private const string DefaultSettingsResourceSuffix = "appsettings-default.json";

    private readonly string _savedCurrentDirectory = Environment.CurrentDirectory;
    private readonly ILogger _savedLogger = Log.Logger;

    public CommandLineHarness()
    {
        StorageFolder = Path.Combine(Path.GetTempPath(), "registry-command-line", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(StorageFolder);
    }

    /// <summary>The temp storage folder (deleted on disposal).</summary>
    public string StorageFolder { get; }

    public string SettingsPath => Path.Combine(StorageFolder, "appsettings.json");

    /// <summary>The embedded <c>appsettings-default.json</c> (what a first start writes to the storage folder).</summary>
    public static JObject DefaultSettings()
    {
        var assembly = typeof(Program).Assembly;
        var name = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith(DefaultSettingsResourceSuffix, StringComparison.Ordinal));

        using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
        return JObject.Parse(reader.ReadToEnd());
    }

    /// <summary>Writes <paramref name="settings"/> as the storage folder's <c>appsettings.json</c>.</summary>
    public void WriteSettings(JObject settings) => File.WriteAllText(SettingsPath, settings.ToString());

    /// <summary>Every file and folder of the storage folder with a content hash (detects any write).</summary>
    public IReadOnlyDictionary<string, string> Snapshot() =>
        Directory.EnumerateFileSystemEntries(StorageFolder, "*", SearchOption.AllDirectories)
            .ToDictionary(p => Path.GetRelativePath(StorageFolder, p),
                p => File.Exists(p) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))) : "<dir>");

    /// <summary>Runs <paramref name="action"/> with stdout and stderr captured.</summary>
    public static (int ExitCode, string Out, string Error) Capture(Func<int> action)
    {
        var savedOut = Console.Out;
        var savedError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        Console.SetOut(output);
        Console.SetError(error);
        try
        {
            var exitCode = action();
            return (exitCode, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(savedOut);
            Console.SetError(savedError);
        }
    }

    public void Dispose()
    {
        Environment.CurrentDirectory = _savedCurrentDirectory;
        Log.Logger = _savedLogger;

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(StorageFolder))
                Directory.Delete(StorageFolder, true);
        }
        catch (IOException)
        {
            // Best effort: the folder lives in temp
        }
    }
}
