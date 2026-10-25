#nullable enable
using System;
using System.Text.Json.Serialization;

namespace Registry.Web.Services.UpdateCheck;

/// <summary>
/// The JSON body of the update check request (<c>Contracts/update-check-payload.v1.schema.json</c> is the contract). A
/// value the deployment does not have is null, except <see cref="ContactEmail"/>, which is left out unless an
/// administrator agreed to be contacted, and the platform values the contract has no name for.
/// </summary>
public sealed record UpdateCheckPayload
{
    /// <summary>The version of the contract.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Always <see cref="CurrentSchemaVersion"/>.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>The identity of the deployment.</summary>
    public required string InstanceId { get; init; }

    /// <summary>When the identity was created (UTC).</summary>
    public DateTime? InstalledAt { get; init; }

    /// <summary>The Registry version (major.minor.patch).</summary>
    public required string Version { get; init; }

    /// <summary>The libddb version this Registry is pinned to.</summary>
    public string? DdbVersion { get; init; }

    /// <summary><c>windows</c> or <c>linux</c>; left out on another system.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Os { get; init; }

    /// <summary>The operating system description, at most 32 characters.</summary>
    public string? OsCaption { get; init; }

    /// <summary><c>x64</c>, <c>arm64</c>, <c>x86</c> or <c>arm</c>; left out on another architecture.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Arch { get; init; }

    /// <summary>The .NET runtime description, at most 32 characters.</summary>
    public string Runtime { get; init; } = string.Empty;

    /// <summary><c>docker</c> or <c>native</c>.</summary>
    public string Env { get; init; } = "native";

    /// <summary>The instance type of the node that sends the request, as the configuration spells it.</summary>
    public string InstanceType { get; init; } = "default";

    /// <summary><c>sqlite</c> or <c>mysql</c>.</summary>
    public string Db { get; init; } = "sqlite";

    /// <summary>The public URL of the deployment (the configured external URL, else the one requests were served on).</summary>
    public string? BaseUrl { get; init; }

    /// <summary>The host name of the node that sends the request, at most 64 characters.</summary>
    public string? Hostname { get; init; }

    /// <summary>The network domain of the node, if it has one.</summary>
    public string? Domain { get; init; }

    /// <summary>Reserved: always null.</summary>
    public string? LicenseRef { get; init; }

    /// <summary>How the administrator says the deployment is used.</summary>
    public string? UseDeclaration { get; init; }

    /// <summary>The e-mail address the administrator agreed to be contacted on; left out without that agreement.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContactEmail { get; init; }
}
