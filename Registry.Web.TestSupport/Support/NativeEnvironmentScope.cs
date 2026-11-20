using System;
using System.Collections.Generic;
using System.Linq;
using Registry.Adapters.DroneDB;

namespace Registry.Web.Test.Support;

/// <summary>
/// Sets environment variables in both the managed and the native (C runtime) environment, so that
/// libddb's <c>getenv</c> observes the same value as managed code, and restores the values the
/// tracked variables had when the scope was created on <see cref="Dispose"/>.
/// </summary>
public sealed class NativeEnvironmentScope : IDisposable
{
    private readonly Dictionary<string, string?> _saved;
    private bool _disposed;

    /// <param name="names">Variables restored on dispose, whoever changes them meanwhile.</param>
    public NativeEnvironmentScope(params string[] names)
    {
        _saved = names.Distinct().ToDictionary(n => n, Environment.GetEnvironmentVariable);
    }

    /// <summary>Sets a variable; a variable not tracked yet is tracked from its current value.</summary>
    public void Set(string name, string? value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _saved.TryAdd(name, Environment.GetEnvironmentVariable(name));
        NativeEnvironment.Set(name, value);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var (name, value) in _saved)
            NativeEnvironment.Set(name, value);
    }
}
