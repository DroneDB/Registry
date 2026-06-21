#nullable enable
using System;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Registry.Web.Models;

namespace Registry.Web.Services.Ports;

/// <summary>
/// Indexed Hangfire enqueue/schedule interface that writes JobIndex rows alongside job creation.
/// </summary>
public interface IIndexedJobEnqueuer
{
    string Enqueue(Expression<Action> methodCall, IndexPayload meta);
    string Enqueue<T>(Expression<Action<T>> methodCall, IndexPayload meta);
    string Enqueue(Expression<Func<Task>> methodCall, IndexPayload meta);
    string Enqueue<T>(Expression<Func<T, Task>> methodCall, IndexPayload meta);

    string Schedule(Expression<Action> methodCall, IndexPayload meta, TimeSpan delay);
    string Schedule<T>(Expression<Action<T>> methodCall, IndexPayload meta, TimeSpan delay);
    string Schedule(Expression<Func<Task>> methodCall, IndexPayload meta, TimeSpan delay);
    string Schedule<T>(Expression<Func<T, Task>> methodCall, IndexPayload meta, TimeSpan delay);
}