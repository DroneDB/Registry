#nullable enable
using System;

namespace Registry.Web.Utilities;

/// <summary>
/// A dataset path that passed libddb's path rule (<see cref="DatasetPathPolicy"/>): normalized ('/' separated,
/// no empty or "." segment, not absolute, no "..", not reserved). Only <see cref="DatasetPathPolicy.Parse"/>
/// makes one, so a value of this type is a checked path.
/// <para>
/// Request paths of this type are checked while the request is bound (<see cref="DatasetPathModelBinder"/> for
/// the query, the route and the form, <see cref="DatasetPathJsonConverter"/> for JSON bodies): a refused path is
/// the rule's 400 before the action and its access check run; a missing or blank one binds nothing.
/// </para>
/// </summary>
/// <remarks>
/// No public property: the API explorer documents a parameter of this type as one string value (see
/// <c>MapType</c> in the OpenAPI setup), not as an object with members.
/// </remarks>
public sealed class DatasetPath : IEquatable<DatasetPath>
{
    private readonly string _value;

    internal DatasetPath(string normalized)
    {
        ArgumentException.ThrowIfNullOrEmpty(normalized);
        _value = normalized;
    }

    /// <summary>The normalized path.</summary>
    public override string ToString() => _value;

    /// <summary>The normalized path, or null for no path.</summary>
    public static implicit operator string?(DatasetPath? path) => path?._value;

    public bool Equals(DatasetPath? other) => other is not null && string.Equals(_value, other._value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is DatasetPath other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(_value);
}
