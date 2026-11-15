using System;
using System.IO;
using System.Threading.Tasks;

namespace Registry.Web.Models;

/// <summary>
/// A file to send that is read while it is sent, never held in memory whole. The receiver owns <see cref="Data"/>:
/// a file result disposes it when the response ends, also when the client goes away; anything else disposes this.
/// </summary>
public sealed class StorageStreamDto : IAsyncDisposable
{
    public required Stream Data { get; init; }
    public required string ContentType { get; init; }
    public required string Name { get; init; }

    public ValueTask DisposeAsync() => Data.DisposeAsync();
}
