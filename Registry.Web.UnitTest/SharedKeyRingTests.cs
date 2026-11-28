using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Import;
using Registry.Web.Test.Support;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// The Data Protection key ring that the web server and the processing nodes share: a node decrypts the
/// credentials of a remote import that the web server encrypted only when both read the same key folder
/// (or Redis), and the production Compose file gives every Registry service the same folder.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~SharedKeyRingTests"
/// </summary>
[TestFixture]
public sealed class SharedKeyRingTests
{
    private const string Credentials = "user:secret";

    private static ImportCredentialProtector Process(string? keysPath)
    {
        var services = new ServiceCollection();
        services.AddSharedDataProtection(new CachingOptions(), new StorageOptions { DataProtectionKeysPath = keysPath });
        return new ImportCredentialProtector(services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>());
    }

    [Test]
    public void ANode_DecryptsWhatTheWebServerEncrypted_WithTheSameKeyFolder()
    {
        var keys = Path.Combine(Path.GetTempPath(), "keyring-" + Guid.NewGuid().ToString("N"));
        try
        {
            var protectedByTheWebServer = Process(keys).Protect(Credentials);

            Process(keys).Unprotect(protectedByTheWebServer).ShouldBe(Credentials);
        }
        finally
        {
            if (Directory.Exists(keys))
                Directory.Delete(keys, true);
        }
    }

    [Test]
    public void ProductionCompose_GivesEveryRegistryServiceTheSameKeyFolder_InTheFolderTheyShare()
    {
        Assume.That(RepositoryFiles.Root, Is.Not.Null, "repository sources not available next to the test output");
        var compose = File.ReadAllText(RepositoryFiles.PathOf("docker/production/docker-compose.yml"));

        var services = Regex.Count(compose, @"- INSTANCE_TYPE=\d\b");
        services.ShouldBeGreaterThan(1);

        var keyFolders = Regex.Matches(compose, @"- AppSettings__DataProtectionKeysPath=(\S+)")
            .Select(m => m.Groups[1].Value).ToArray();
        keyFolders.Length.ShouldBe(services, "each Registry service sets the key folder");
        keyFolders.Distinct().ShouldHaveSingleItem().ShouldStartWith("/data/");
        Regex.Count(compose, @"- \./data:/data\s").ShouldBe(services, "each Registry service mounts the shared ./data");
    }
}
