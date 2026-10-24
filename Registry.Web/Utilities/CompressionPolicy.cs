#nullable enable
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace Registry.Web.Utilities;

/// <summary>
/// Which responses the dynamic compression leaves alone. A compressed response that carries a
/// secret next to text an attacker can influence leaks the secret through its size over TLS
/// (BREACH), so the responses that return tokens (sign-in, password change, share and push
/// tokens, batch listings) are sent uncompressed. They are small.
/// </summary>
public static partial class CompressionPolicy
{
    [GeneratedRegex(@"^/(users/(authenticate(/refresh)?|changepwd|[^/]+/changepwd)|share/(init|info/[^/]+)|orgs/[^/]+/ds/[^/]+/(push/init|batches)|sys/cleanupbatches)/?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex SecretBearingPath();

    /// <summary>Whether the response of a request to <paramref name="path"/> carries a token.</summary>
    public static bool CarriesSecrets(PathString path) => path.HasValue && SecretBearingPath().IsMatch(path.Value!);
}
