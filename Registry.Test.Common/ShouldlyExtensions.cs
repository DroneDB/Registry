using System.Collections;
using System.Reflection;
using System.Text.Json;
using Shouldly;

namespace Registry.Test.Common;

/// <summary>
/// Extension methods for Shouldly to provide functionality similar to FluentAssertions' BeEquivalentTo
/// </summary>
public static class ShouldlyExtensions
{
    /// <summary>
    /// Verifies that two objects are equivalent by comparing their properties recursively.
    /// This provides similar functionality to FluentAssertions' BeEquivalentTo method.
    /// </summary>
    /// <typeparam name="T">The type of the actual object</typeparam>
    /// <param name="actual">The actual object to compare</param>
    /// <param name="expected">The expected object to compare against</param>
    /// <param name="customMessage">Optional custom message for assertion failures</param>
    public static void ShouldBeEquivalentTo<T>(this T actual, T expected, string? customMessage = null)
    {
        if (actual == null && expected == null)
            return;

        if (actual == null || expected == null)
        {
            var message = customMessage ?? "Objects should be equivalent";
            throw new ShouldAssertException($"{message}: one object is null and the other is not");
        }

        CompareObjects(actual, expected, typeof(T).Name, customMessage);
    }

    private static void CompareObjects(object? actual, object? expected, string path, string? customMessage)
    {
        // Handle null cases
        if (actual == null && expected == null)
            return;

        if (actual == null || expected == null)
        {
            var message = customMessage ?? $"Objects at {path} should be equivalent";
            throw new ShouldAssertException($"{message}: one is null and the other is not");
        }

        var type = actual.GetType();

        // JSON documents (the ports carry libddb's as JsonElement): equal when they hold the same values
        if (actual is JsonElement actualJson && expected is JsonElement expectedJson)
        {
            if (!JsonEquivalent(actualJson, expectedJson))
            {
                var message = customMessage ?? $"JSON at {path} should be equivalent";
                throw new ShouldAssertException($"{message}: expected {expectedJson.GetRawText()} but was {actualJson.GetRawText()}");
            }
            return;
        }

        // Handle primitive types and strings
        if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal) ||
            type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan) ||
            type == typeof(Guid))
        {
            if (!actual.Equals(expected))
            {
                var message = customMessage ?? $"Values at {path} should be equivalent";
                throw new ShouldAssertException($"{message}: expected {expected} but was {actual}");
            }
            return;
        }

        // Handle enumerables (arrays, lists, collections)
        if (actual is IEnumerable actualEnumerable && expected is IEnumerable expectedEnumerable)
        {
            var actualList = actualEnumerable.Cast<object>().ToList();
            var expectedList = expectedEnumerable.Cast<object>().ToList();

            if (actualList.Count != expectedList.Count)
            {
                var message = customMessage ?? $"Collections at {path} should have the same count";
                throw new ShouldAssertException($"{message}: expected {expectedList.Count} items but was {actualList.Count}");
            }

            for (int i = 0; i < actualList.Count; i++)
            {
                CompareObjects(actualList[i], expectedList[i], $"{path}[{i}]", customMessage);
            }
            return;
        }

        // Handle complex objects by comparing properties
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var property in properties)
        {
            if (!property.CanRead)
                continue;

            var actualValue = property.GetValue(actual);
            var expectedValue = property.GetValue(expected);

            CompareObjects(actualValue, expectedValue, $"{path}.{property.Name}", customMessage);
        }
    }

    /// <summary>
    /// Same structure, same member names (any order), same values; numbers within a relative 1e-9, because two
    /// libddb builds may print the last digit of a computed coordinate differently.
    /// </summary>
    public static bool JsonEquivalent(JsonElement actual, JsonElement expected)
    {
        if (actual.ValueKind != expected.ValueKind)
            return false;

        switch (actual.ValueKind)
        {
            case JsonValueKind.Object:
                var members = actual.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
                var others = expected.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
                return members.Count == others.Count &&
                       members.All(m => others.TryGetValue(m.Key, out var other) && JsonEquivalent(m.Value, other));
            case JsonValueKind.Array:
                return actual.GetArrayLength() == expected.GetArrayLength() &&
                       actual.EnumerateArray().Zip(expected.EnumerateArray(), JsonEquivalent).All(equal => equal);
            case JsonValueKind.Number:
                var a = actual.GetDouble();
                var e = expected.GetDouble();
                return Math.Abs(a - e) <= 1e-9 * Math.Max(1, Math.Abs(e));
            case JsonValueKind.String:
                return actual.GetString() == expected.GetString();
            default:
                return true;
        }
    }
}
