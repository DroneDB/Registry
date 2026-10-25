#nullable enable
using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Registry.Web.Services.Ports;

/// <summary>
/// The identity of a deployment: a random id created at the first start and kept in the storage folder, which every
/// node of the deployment shares, so replicas and processing nodes report one identity. It is the content of the
/// <c>.instance-id</c> file.
/// </summary>
/// <param name="Id">The random identifier (a UUID). It is the value to quote in a request to delete the data held about the deployment.</param>
/// <param name="InstalledAt">When the identity was created: the first start of the deployment (UTC).</param>
public sealed record InstanceIdentity(string Id, DateTime InstalledAt)
{
    private const string HashPrefix = "ddb-instance-v1:";

    /// <summary>
    /// The hash a deployment publishes (<c>GET /sys/features</c>) to prove it is the one with <see cref="Id"/>, without
    /// revealing the id: lowercase hex SHA-256 of <c>ddb-instance-v1:</c> and the lowercase id.
    /// </summary>
    [JsonIgnore]
    public string Hash => HashOf(Id);

    /// <summary>The published hash of the identity <paramref name="id"/> (see <see cref="Hash"/>).</summary>
    public static string HashOf(string id) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(HashPrefix + id.ToLowerInvariant())));
}

/// <summary>The one reader of the deployment's identity file.</summary>
public interface IInstanceIdentity
{
    /// <summary>
    /// The identity of this deployment. The first call of a deployment creates it; every node, then and later, gets the
    /// same one from the shared storage folder.
    /// </summary>
    InstanceIdentity Current { get; }
}
