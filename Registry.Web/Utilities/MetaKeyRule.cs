#nullable enable
using System;

namespace Registry.Web.Utilities;

/// <summary>What a metadata key names: one value (replaced by <c>set</c>) or a list (appended to by <c>add</c>).</summary>
public enum MetaKeyKind
{
    /// <summary>One item per path and key: written by <c>set</c>.</summary>
    Single,

    /// <summary>Several items per path and key: written by <c>add</c>.</summary>
    List
}

/// <summary>
/// libddb's metadata key rule (<c>metakey::isList</c> and <c>MetaManager::getKey</c> in DroneDB), the one place
/// Registry reads it: a key that ends with <c>s</c> names a list, any other key names a single item; a key is
/// not empty and has no uppercase ASCII letter.
/// <para>
/// Why Registry checks it: libddb rejects a key that breaks the rule with <c>DDBERR_EXCEPTION</c> and a message
/// (the <c>DDBMeta*</c> exports keep the codes they always returned, ABI 1.1), so the call can only be a 500 for
/// Registry unless it knows the rule is the reason. The messages are libddb's own, and the corpus test of
/// the integration project (<c>Rq15MetaKeyRuleTests</c>) runs every key through the real library, so a rule that
/// drifts from libddb's fails there. When the <c>DDBMeta*</c> exports return <c>DDBERR_INVALIDARGS</c> (a later
/// ABI), this class goes and <c>DdbResultMapper</c> answers 400.
/// </para>
/// </summary>
public static class MetaKeyRule
{
    private const char ListSuffix = 's';

    /// <summary>True when <paramref name="key"/> names a list (libddb's <c>metakey::isList</c>).</summary>
    public static bool IsList(string key) => key.Length > 0 && key[^1] == ListSuffix;

    /// <summary>
    /// What is wrong with <paramref name="key"/> as a key written as <paramref name="kind"/> (libddb's message),
    /// or null when libddb accepts it.
    /// </summary>
    public static string? Violation(string key, MetaKeyKind kind)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (key.Length == 0)
            return "Invalid empty metadata key";

        foreach (var c in key)
            if (c is >= 'A' and <= 'Z')
                return "Metadata key must be lowercase";

        return (kind, IsList(key)) switch
        {
            (MetaKeyKind.List, false) => $"Invalid metadata key (must be plural, for example: {key}s)",
            (MetaKeyKind.Single, true) => $"Invalid metadata key (must be singular, for example: {key[..^1]})",
            _ => null
        };
    }
}
