#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Registry.Web.Test.Support;

/// <summary>
/// Builds deterministic instances of the wire types (RQ-14): every member filled ("full"), or every
/// reference member null and every value member default ("sparse"). The wire shape test serializes
/// both through the host's formatter, so a member's name, nullability, enum form, date form, number
/// form and ordering are all pinned without a hand-written case per DTO.
/// <para>
/// Members that hold a JSON document (<c>object</c>, <c>JsonElement</c>, Json.NET tokens) are filled by
/// the <c>jsonLiteral</c> factory the test supplies: it knows which JSON stack the host's types use.
/// </para>
/// </summary>
public sealed class WirePopulator(Func<Type, string, object?> jsonLiteral)
{
    /// <summary>The JSON a document member holds: numbers (whole and fractional), nested objects, nulls, text.</summary>
    public const string DocumentJson =
        """{"snake_case":1.5,"whole":2.0,"count":3,"List":[1,2.5,"text",true,null],"nested":{"Pascal":null,"flag":false}}""";

    private const int MaxDepth = 5;

    private static readonly double[] Doubles = [1.0, 2.5, 0.00001, 123456789.125];
    private static readonly DateTime Utc = new(2026, 10, 21, 7, 31, 17, 123, DateTimeKind.Utc);
    private static readonly DateTime Unspecified = new(2026, 10, 21, 7, 31, 17, DateTimeKind.Unspecified);

    private int _doubles;
    private int _dates;
    private bool _utcOnly;

    /// <summary>Whether the type can be populated (a constructor or public setters, not abstract).</summary>
    public static bool CanPopulate(Type type) =>
        !type.IsAbstract && !type.IsInterface && !type.IsGenericTypeDefinition &&
        (type.IsValueType || type.GetConstructors().Length > 0);

    public object? Populate(Type type, bool full, string member = "value", int depth = 0)
    {
        if (depth > MaxDepth)
            return null;

        var nullable = Nullable.GetUnderlyingType(type);
        if (nullable != null)
            return full ? Populate(nullable, full, member, depth) : null;

        if (IsDocument(type))
        {
            if (full)
                return jsonLiteral(type, DocumentJson);

            // A JsonElement that is not nullable has no "absent": its default is not a value, JSON null is
            return type.FullName is "System.Text.Json.JsonElement" ? jsonLiteral(type, "null") : null;
        }

        if (type == typeof(string))
            return full ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{member}-text") : null;
        if (type == typeof(bool)) return full;
        if (type == typeof(int)) return full ? 42 : 0;
        if (type == typeof(long)) return full ? 1234567890123L : 0L;
        if (type == typeof(short)) return full ? (short)7 : (short)0;
        if (type == typeof(byte)) return full ? (byte)5 : (byte)0;
        if (type == typeof(uint)) return full ? 42u : 0u;
        if (type == typeof(ulong)) return full ? 42ul : 0ul;
        if (type == typeof(double)) return full ? Doubles[_doubles++ % Doubles.Length] : 0d;
        if (type == typeof(float)) return full ? 1.5f : 0f;
        if (type == typeof(decimal)) return full ? 10.0m : 0m;
        if (type == typeof(char)) return full ? 'x' : '\0';
        if (type == typeof(DateTime)) return full ? (_utcOnly || _dates++ % 2 == 0 ? Utc : Unspecified) : default(DateTime);
        if (type == typeof(DateTimeOffset)) return full ? new DateTimeOffset(Unspecified, TimeSpan.FromHours(2)) : default;
        if (type == typeof(TimeSpan)) return full ? new TimeSpan(1, 2, 3, 4) : default;
        if (type == typeof(Guid)) return full ? new Guid("0a223495-84a0-4c15-b425-c7ef88110e75") : default;
        if (type == typeof(Uri)) return full ? new Uri("https://example.test/x?y=1") : null;
        if (type == typeof(byte[])) return full ? new byte[] { 1, 2, 3 } : null;
        if (type.IsEnum) return Enumeration(type, full);

        if (TryDictionary(type, full, member, depth, out var dictionary)) return dictionary;
        if (TryCollection(type, full, member, depth, out var collection)) return collection;

        // The root of a sparse variant is built (its members are null); anything below it is absent
        if (!full && !type.IsValueType && depth > 0)
            return null;

        return Object(type, full, depth);
    }

    private static bool IsDocument(Type type) =>
        type == typeof(object) || type.FullName is "System.Text.Json.JsonElement" ||
        type.FullName is { } name && (name.StartsWith("Newtonsoft.Json.Linq.J", StringComparison.Ordinal) ||
                                      name.StartsWith("System.Text.Json.Nodes.Json", StringComparison.Ordinal));

    private static object Enumeration(Type type, bool full)
    {
        var values = Enum.GetValues(type).Cast<object>()
            .Where(v => Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture) != 0)
            .OrderBy(v => Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        return full && values.Length > 0 ? values[0] : Activator.CreateInstance(type)!;
    }

    private bool TryDictionary(Type type, bool full, string member, int depth, out object? result)
    {
        result = null;
        var dictionary = Generic(type, typeof(IDictionary<,>)) ?? Generic(type, typeof(IReadOnlyDictionary<,>));
        if (dictionary == null || dictionary.GetGenericArguments()[0] != typeof(string))
            return false;
        if (!full)
            return true;

        var valueType = dictionary.GetGenericArguments()[1];
        var concrete = type.IsInterface || type.IsAbstract ? typeof(Dictionary<,>).MakeGenericType(typeof(string), valueType) : type;
        var instance = (IDictionary)Activator.CreateInstance(concrete)!;
        foreach (var key in new[] { "Alpha", "snake_Key" })
            instance[key] = Populate(valueType, true, key, depth + 1);
        result = instance;
        return true;
    }

    private bool TryCollection(Type type, bool full, string member, int depth, out object? result)
    {
        result = null;
        Type? element = type.IsArray ? type.GetElementType() : Generic(type, typeof(IEnumerable<>))?.GetGenericArguments()[0];
        if (element == null)
            return false;
        if (!full)
            return true;

        var item = Populate(element, true, member, depth + 1);
        if (type.IsArray)
        {
            var array = Array.CreateInstance(element, 1);
            array.SetValue(item, 0);
            result = array;
            return true;
        }

        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element))!;
        list.Add(item);
        if (type.IsAssignableFrom(list.GetType()))
        {
            result = list;
            return true;
        }

        // IReadOnlySet<T>, ISet<T>: a hash set
        var set = Activator.CreateInstance(typeof(HashSet<>).MakeGenericType(element), list);
        if (type.IsInstanceOfType(set))
        {
            result = set;
            return true;
        }

        // HashSet<T>, ImmutableArray<T>, ...: a constructor taking the sequence
        result = Activator.CreateInstance(type, list);
        return true;
    }

    private object? Object(Type type, bool full, int depth)
    {
        if (!CanPopulate(type))
            return null;
        if (type.IsValueType && !full)
            return Activator.CreateInstance(type);

        var ctor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        var parameters = ctor.GetParameters();
        // Constructor arguments are what the host builds from its own clock (UTC); setters model EF-loaded
        // values, whose kind is Unspecified: both kinds are written, and neither depends on the machine's time zone
        object instance;
        var outer = _utcOnly;
        _utcOnly = true;
        try
        {
            try
            {
                instance = ctor.Invoke(parameters.Select(p => Populate(p.ParameterType, full, p.Name ?? "arg", depth + 1)).ToArray());
            }
            catch (TargetInvocationException) when (!full)
            {
                // A constructor that dereferences its arguments: sparse members, full arguments
                instance = ctor.Invoke(parameters.Select(p => Populate(p.ParameterType, true, p.Name ?? "arg", depth + 1)).ToArray());
            }
        }
        finally
        {
            _utcOnly = outer;
        }

        var set = parameters.Select(p => p.Name!.ToLowerInvariant()).ToHashSet();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.CanWrite && p.SetMethod!.IsPublic && p.GetIndexParameters().Length == 0 &&
                                 !set.Contains(p.Name.ToLowerInvariant())))
            property.SetValue(instance, Populate(property.PropertyType, full, property.Name, depth + 1));

        return instance;
    }

    private static Type? Generic(Type type, Type definition) =>
        (type.IsGenericType && type.GetGenericTypeDefinition() == definition ? type : null) ??
        type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == definition);
}
