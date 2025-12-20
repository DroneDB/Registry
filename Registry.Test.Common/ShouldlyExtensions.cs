using System.Collections;
using System.Reflection;
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
}
