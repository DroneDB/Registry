using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Shouldly;

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
        SetVariable(name, value);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var (name, value) in _saved)
            SetVariable(name, value);
    }

    /// <summary>Sets (or, for <see langword="null"/>, removes) a variable in the managed and native environment.</summary>
    public static void SetVariable(string name, string? value)
    {
        Environment.SetEnvironmentVariable(name, value);
        var result = OperatingSystem.IsWindows() ? PutEnv(name, value ?? "") :
            value == null ? UnsetEnv(name) : SetEnv(name, value, 1);
        result.ShouldBe(0);
    }

    [DllImport("libc", EntryPoint = "setenv")]
    private static extern int SetEnv(string name, string value, int overwrite);

    [DllImport("libc", EntryPoint = "unsetenv")]
    private static extern int UnsetEnv(string name);

    [DllImport("ucrtbase", EntryPoint = "_putenv_s", CallingConvention = CallingConvention.Cdecl)]
    private static extern int PutEnv(string name, string value);
}
