#nullable enable
using System;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Registry.Web.Models;

namespace Registry.Web.Services.Ports;

public interface IIndexedJobEnqueuer
{
    string Enqueue(Expression<Action> methodCall, IndexPayload meta);
    string Enqueue<T>(Expression<Action<T>> methodCall, IndexPayload meta);
    string Enqueue(Expression<Func<Task>> methodCall, IndexPayload meta);
    string Enqueue<T>(Expression<Func<T, Task>> methodCall, IndexPayload meta);
}