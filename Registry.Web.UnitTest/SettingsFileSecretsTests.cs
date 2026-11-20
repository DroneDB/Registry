using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Registry.Adapters;
using Registry.Web.Hosting;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// The secrets a start generates into the settings file of the storage folder (the JWT secret and the thumbnail
/// generator token): one that the environment sets is not generated, so the file is not written and may be read-only;
/// one that neither gives, with a file that cannot be written, stops the start with a message that names the key and the
/// two ways to set it; with a writable file it is generated once and kept.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~SettingsFileSecretsTests"
/// </summary>
[TestFixture]
public class SettingsFileSecretsTests
{
    private const string Token = "AppSettings:ThumbnailGeneratorToken";
    private const string Secret = "AppSettings:Secret";

    private string _folder = null!;
    private string _file = null!;

    [SetUp]
    public void SetUp()
    {
        _folder = Path.Combine(Path.GetTempPath(), "settings-secrets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
        _file = SettingsFile.PathIn(_folder);
    }

    [TearDown]
    public void TearDown()
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.Delete(_folder, true);
    }

    // A settings file whose AppSettings section has the given secrets
    private string WriteSettings(params (string Key, string Value)[] secrets)
    {
        var section = new JsonObject();
        foreach (var (key, value) in secrets)
            section[key.Split(':')[1]] = value;
        var json = new JsonObject { ["AppSettings"] = section }.ToJsonString();
        File.WriteAllText(_file, json);
        return json;
    }

    private static IConfiguration Environment(params (string Key, string Value)[] variables)
    {
        var values = new Dictionary<string, string?>();
        foreach (var (key, value) in variables)
            values[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    // The folder and its file can no longer be written (false when this user writes anyway: root)
    private bool MakeReadOnly()
    {
        if (OperatingSystem.IsWindows())
            return false;
        File.SetUnixFileMode(_file, UnixFileMode.UserRead);
        File.SetUnixFileMode(_folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            File.WriteAllText(Path.Combine(_folder, "probe"), "x");
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    [Test]
    public void SecretsSetInTheEnvironment_AreNotGenerated_AndTheReadOnlyFileIsNotWritten()
    {
        var content = WriteSettings();
        if (!MakeReadOnly())
            Assert.Ignore("The file stays writable for this user");

        var changes = SettingsFile.Prepare(_folder, new FileSystem(),
            Environment((Token, "token-from-the-environment"), (Secret, "secret-from-the-environment")));

        changes.GeneratedSecrets.ShouldBeEmpty();
        changes.Created.ShouldBeFalse();
        File.ReadAllText(_file).ShouldBe(content);
    }

    [Test]
    public void ATokenSetInTheEnvironment_IsNotWrittenToTheFile_TheOtherMissingSecretIs()
    {
        WriteSettings();

        var changes = SettingsFile.Prepare(_folder, new FileSystem(), Environment((Token, "token-from-the-environment")));

        changes.GeneratedSecrets.ShouldBe(["Secret"]);
        var section = JsonNode.Parse(File.ReadAllText(_file))!["AppSettings"]!.AsObject();
        section.ContainsKey("ThumbnailGeneratorToken").ShouldBeFalse();
        section["Secret"]!.GetValue<string>().Length.ShouldBe(SettingsFile.SecretLength);
    }

    [Test]
    public void AMissingToken_WithAReadOnlyFile_StopsTheStart_NamingTheKeyAndBothWaysToSetIt()
    {
        var content = WriteSettings((Secret, "a-secret-in-the-file"));
        if (!MakeReadOnly())
            Assert.Ignore("The file stays writable for this user");

        var ex = Should.Throw<IOException>(() => SettingsFile.Prepare(_folder, new FileSystem(), Environment()));

        ex.Message.ShouldContain("AppSettings:ThumbnailGeneratorToken is not set");
        ex.Message.ShouldContain($"'{_file}' cannot be written");
        ex.Message.ShouldContain("AppSettings__ThumbnailGeneratorToken");
        ex.Message.ShouldNotContain("AppSettings:Secret");
        File.ReadAllText(_file).ShouldBe(content);
    }

    [Test]
    public void AMissingToken_WithAWritableFile_IsGeneratedOnce_AndKept()
    {
        WriteSettings((Secret, "a-secret-in-the-file"));

        var first = SettingsFile.Prepare(_folder, new FileSystem(), Environment());
        var token = JsonNode.Parse(File.ReadAllText(_file))!["AppSettings"]!["ThumbnailGeneratorToken"]!.GetValue<string>();
        var second = SettingsFile.Prepare(_folder, new FileSystem(), Environment());

        first.GeneratedSecrets.ShouldBe(["ThumbnailGeneratorToken"]);
        token.Length.ShouldBe(SettingsFile.SecretLength);
        second.GeneratedSecrets.ShouldBeEmpty();
        JsonNode.Parse(File.ReadAllText(_file))!["AppSettings"]!["ThumbnailGeneratorToken"]!.GetValue<string>()
            .ShouldBe(token);
        JsonNode.Parse(File.ReadAllText(_file))!["AppSettings"]!["Secret"]!.GetValue<string>()
            .ShouldBe("a-secret-in-the-file");
    }
}
