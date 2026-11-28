using System;
using System.Collections.Concurrent;
using Registry.Ports.DroneDB;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// The libddb operation ids of the HTTP requests being served: one per request, mapped to the request id
/// (<c>HttpContext.TraceIdentifier</c>, the <c>RequestId</c> property Serilog writes on every record logged while
/// the request is served), so a native log record (<see cref="DdbLogSink"/>) names the request it was made for and
/// the host's records and libddb's can be read together. The jobs have theirs in
/// <see cref="Jobs.JobOperations"/>; both take their ids from <see cref="DdbOperation.NewId"/>.
/// </summary>
public static class RequestOperations
{
    private static readonly ConcurrentDictionary<ulong, string> Running = new();

    /// <summary>
    /// Starts the operation of the request <paramref name="requestId"/> and makes it the current one
    /// (<see cref="DdbOperation"/>) until the returned scope is disposed. Begin and dispose in the same flow.
    /// </summary>
    public static Scope Begin(string requestId)
    {
        ArgumentNullException.ThrowIfNull(requestId);

        var id = DdbOperation.NewId();
        Running[id] = requestId;
        return new Scope(id, DdbOperation.Enter(id));
    }

    /// <summary>The request id of the running operation <paramref name="id"/>, or null.</summary>
    public static string? Find(ulong id) => id != 0 && Running.TryGetValue(id, out var requestId) ? requestId : null;

    /// <summary>Ends the request's operation and restores the previous current one when disposed.</summary>
    public readonly struct Scope : IDisposable
    {
        private readonly ulong _id;
        private readonly DdbOperation.Scope _scope;

        internal Scope(ulong id, DdbOperation.Scope scope)
        {
            _id = id;
            _scope = scope;
        }

        public void Dispose()
        {
            Running.TryRemove(_id, out _);
            _scope.Dispose();
        }
    }
}
