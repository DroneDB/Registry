using System.Reflection;
using System.Security.Cryptography;

namespace Registry.Adapters.Testing;

/// <summary>
/// The libddb contract files vendored from DroneDB <c>tests/contract</c> (XI-09), embedded byte for
/// byte. <see cref="Source"/> records the DroneDB commit they come from and the sha256 of each file.
/// </summary>
public static class ContractFiles
{
    /// <summary>The manifest file, in <c>Contract/</c>, that is not itself vendored.</summary>
    public const string SourceFileName = "SOURCE";

    private const string ResourcePrefix = "Contract/";

    private static readonly Assembly Assembly = typeof(ContractFiles).Assembly;

    // Resource name -> path relative to Contract/ with '/' separators (RecursiveDir uses '\' on Windows)
    private static readonly IReadOnlyDictionary<string, string> Resources = Assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
        .ToDictionary(n => n[ResourcePrefix.Length..].Replace('\\', '/'), n => n, StringComparer.Ordinal);

    private static readonly Lazy<ContractSource> LazySource = new(() => ContractSource.Parse(ReadText(SourceFileName)));

    /// <summary>The paths (relative to <c>Contract/</c>) of every embedded file, <see cref="SourceFileName"/> included.</summary>
    public static IReadOnlyCollection<string> Paths => [.. Resources.Keys.Order(StringComparer.Ordinal)];

    /// <summary>The parsed <c>Contract/SOURCE</c>.</summary>
    public static ContractSource Source => LazySource.Value;

    /// <summary>The bytes of the embedded file <paramref name="path"/> (relative to <c>Contract/</c>).</summary>
    public static byte[] ReadBytes(string path)
    {
        if (!Resources.TryGetValue(path, out var resource))
            throw new FileNotFoundException($"'{path}' is not a vendored contract file (Registry.Adapters.Testing/Contract)", path);

        using var stream = Assembly.GetManifestResourceStream(resource)
                           ?? throw new FileNotFoundException($"Missing embedded resource '{resource}'", path);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>The UTF-8 text of the embedded file <paramref name="path"/>.</summary>
    public static string ReadText(string path) => System.Text.Encoding.UTF8.GetString(ReadBytes(path));

    /// <summary>The lowercase hex sha256 of the embedded file <paramref name="path"/>.</summary>
    public static string Sha256(string path) => Convert.ToHexStringLower(SHA256.HashData(ReadBytes(path)));
}

/// <summary>
/// <c>Contract/SOURCE</c>: where the vendored files come from and their sha256 (written by
/// <c>update-contract.sh</c>). Lines: <c># comment</c>, <c>key: value</c> or <c>&lt;sha256&gt;  &lt;path&gt;</c>.
/// </summary>
public sealed record ContractSource(string Repository, string Commit, string Path, IReadOnlyDictionary<string, string> Hashes)
{
    public static ContractSource Parse(string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            // sha256sum format: 64 hex digits, two spaces, the path
            if (line.Length > 66 && line[64] == ' ' && line[65] == ' ' && line[..64].All(Uri.IsHexDigit))
            {
                if (!hashes.TryAdd(line[66..], line[..64]))
                    throw new FormatException($"SOURCE lists '{line[66..]}' twice");
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
                throw new FormatException($"Unexpected SOURCE line: '{line}'");
            fields[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        string Field(string name) => fields.TryGetValue(name, out var value) && value.Length > 0
            ? value
            : throw new FormatException($"SOURCE has no '{name}:' line");

        return new ContractSource(Field("repository"), Field("commit"), Field("path"), hashes);
    }
}
