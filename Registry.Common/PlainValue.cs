#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Registry.Common;

/// <summary>
/// Value semantics for plain JSON-like values: what a free-form dictionary such as <c>User.Metadata</c> holds. The domain is
/// <c>null</c>, <see cref="string"/>, <see cref="bool"/>, integers and floating-point numbers, <see cref="JsonElement"/>,
/// dictionaries with string keys and lists; any other value is compared with <see cref="object.Equals(object)"/>.
/// <para>
/// Equality, hash and copy are by content, which is what EF's value comparer and "did the provider send something new"
/// need: dictionaries are unordered (same keys, same values), lists are ordered, an integer is one kind and a floating-point
/// number another (<c>1</c> and <c>1.0</c> are stored differently), whatever their CLR type (<c>int</c> and <c>long</c> are
/// the same integer), and a <see cref="JsonElement"/> is what its JSON says (<see cref="From"/>).
/// </para>
/// </summary>
public static class PlainValue
{
    /// <summary>True when <paramref name="a"/> and <paramref name="b"/> hold the same values.</summary>
    public static bool AreEqual(object? a, object? b)
    {
        a = Normalize(a);
        b = Normalize(b);

        if (ReferenceEquals(a, b))
            return true;
        if (a is null || b is null)
            return false;

        return (a, b) switch
        {
            (string x, string y) => string.Equals(x, y, StringComparison.Ordinal),
            (bool x, bool y) => x == y,
            (long x, long y) => x == y,
            (double x, double y) => x.Equals(y),
            (IDictionary<string, object?> x, IDictionary<string, object?> y) => DictionariesAreEqual(x, y),
            (IEnumerable x, IEnumerable y) when IsList(a) && IsList(b) => ListsAreEqual(x, y),
            _ => a.GetType() == b.GetType() && a.Equals(b)
        };
    }

    /// <summary>A hash of <paramref name="value"/> that is the same for values <see cref="AreEqual"/> calls equal.</summary>
    public static int GetHashCode(object? value)
    {
        value = Normalize(value);

        switch (value)
        {
            case null:
                return 0;
            case string s:
                return StringComparer.Ordinal.GetHashCode(s);
            case IDictionary<string, object?> map:
            {
                // Unordered: the sum of the entries' hashes
                var hash = map.Count;
                foreach (var (key, item) in map)
                    hash += HashCode.Combine(StringComparer.Ordinal.GetHashCode(key), GetHashCode(item));
                return hash;
            }
            case IEnumerable list when IsList(value):
            {
                var hash = new HashCode();
                foreach (var item in list)
                    hash.Add(GetHashCode(item));
                return hash.ToHashCode();
            }
            default:
                return value.GetHashCode();
        }
    }

    /// <summary>
    /// A deep copy: new dictionaries and lists, the same immutable values. A dictionary keeps its comparer; other lists are
    /// copied into <see cref="List{T}"/>.
    /// </summary>
    public static object? Clone(object? value) => value switch
    {
        IDictionary<string, object?> map => CloneDictionary(map),
        IEnumerable list when IsList(list) => CloneList(list),
        _ => value
    };

    /// <summary>A deep copy of <paramref name="map"/> (see <see cref="Clone(object?)"/>).</summary>
    public static Dictionary<string, object> Clone(Dictionary<string, object> map) =>
        (Dictionary<string, object>)(object)CloneDictionary((IDictionary<string, object?>)map);

    /// <summary>
    /// The plain value of <paramref name="element"/>: <c>null</c>, <see cref="string"/>, <see cref="bool"/>, <see cref="long"/>
    /// (an integer that fits), <see cref="double"/> (any other number), <see cref="List{T}"/> of values and
    /// <see cref="Dictionary{TKey,TValue}"/> of values (the last of a repeated name wins). Strings are never read as dates.
    /// </summary>
    public static object? From(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => ObjectOf(element),
        JsonValueKind.Array => ArrayOf(element),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var number) ? (object)number : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };

    // Integers are long, floating-point numbers double, JSON is its plain value: what is compared and hashed
    private static object? Normalize(object? value) => value switch
    {
        JsonElement element => From(element),
        sbyte or byte or short or ushort or int or uint => Convert.ToInt64(value, CultureInfo.InvariantCulture),
        long => value,
        ulong u when u <= long.MaxValue => (long)u,
        float f => (double)f,
        decimal m => (double)m,
        _ => value
    };

    private static bool IsList(object value) => value is IEnumerable and not string and not IDictionary<string, object?>
        and not IDictionary;

    private static bool DictionariesAreEqual(IDictionary<string, object?> x, IDictionary<string, object?> y)
    {
        if (x.Count != y.Count)
            return false;

        foreach (var (key, item) in x)
            if (!y.TryGetValue(key, out var other) || !AreEqual(item, other))
                return false;

        return true;
    }

    private static bool ListsAreEqual(IEnumerable x, IEnumerable y)
    {
        var left = x.GetEnumerator();
        var right = y.GetEnumerator();
        try
        {
            while (true)
            {
                var hasLeft = left.MoveNext();
                if (hasLeft != right.MoveNext())
                    return false;
                if (!hasLeft)
                    return true;
                if (!AreEqual(left.Current, right.Current))
                    return false;
            }
        }
        finally
        {
            (left as IDisposable)?.Dispose();
            (right as IDisposable)?.Dispose();
        }
    }

    private static Dictionary<string, object?> CloneDictionary(IDictionary<string, object?> map)
    {
        var copy = map is Dictionary<string, object?> source
            ? new Dictionary<string, object?>(source.Count, source.Comparer)
            : new Dictionary<string, object?>(map.Count, StringComparer.Ordinal);
        foreach (var (key, item) in map)
            copy[key] = Clone(item);
        return copy;
    }

    private static List<object?> CloneList(IEnumerable list)
    {
        var copy = new List<object?>();
        foreach (var item in list)
            copy.Add(Clone(item));
        return copy;
    }

    private static Dictionary<string, object?> ObjectOf(JsonElement element)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            map[property.Name] = From(property.Value);
        return map;
    }

    private static List<object?> ArrayOf(JsonElement element)
    {
        var list = new List<object?>(element.GetArrayLength());
        foreach (var item in element.EnumerateArray())
            list.Add(From(item));
        return list;
    }
}
