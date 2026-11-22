using System;

namespace Registry.Web.Test.Support;

/// <summary>
/// A progress that reports on the caller's thread, at once: <see cref="Progress{T}"/> posts its reports, so a test that
/// reads them after the call would race.
/// </summary>
public sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
