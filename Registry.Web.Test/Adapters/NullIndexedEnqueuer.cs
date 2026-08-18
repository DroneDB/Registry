using System;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Registry.Web.Models;
using Registry.Web.Services.Ports;

namespace Registry.Web.Test.Adapters;

/// <summary>
/// Shared no-op <see cref="IIndexedJobEnqueuer"/> for unit-test doubles that must not index
/// job runs (previously copy-pasted as a private nested class in RetryEndpointTest and
/// BackgroundJobsProcessorTest - review round 2, finding 3/D4).
/// </summary>
internal sealed class NullIndexedEnqueuer : IIndexedJobEnqueuer
{
    private NullIndexedEnqueuer() { }

    public static NullIndexedEnqueuer Create() => new();

    public string Enqueue(Expression<Action> methodCall, IndexPayload meta) => throw new NotImplementedException();
    public string Enqueue<T>(Expression<Action<T>> methodCall, IndexPayload meta) => throw new NotImplementedException();
    public string Enqueue(Expression<Func<Task>> methodCall, IndexPayload meta) => throw new NotImplementedException();
    public string Enqueue<T>(Expression<Func<T, Task>> methodCall, IndexPayload meta) => throw new NotImplementedException();
    public string Schedule(Expression<Action> methodCall, IndexPayload meta, TimeSpan delay) => throw new NotImplementedException();
    public string Schedule<T>(Expression<Action<T>> methodCall, IndexPayload meta, TimeSpan delay) => throw new NotImplementedException();
    public string Schedule(Expression<Func<Task>> methodCall, IndexPayload meta, TimeSpan delay) => throw new NotImplementedException();
    public string Schedule<T>(Expression<Func<T, Task>> methodCall, IndexPayload meta, TimeSpan delay) => throw new NotImplementedException();
}
