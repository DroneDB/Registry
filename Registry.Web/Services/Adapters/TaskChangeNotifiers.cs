#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Registry.Web.Services.Ports;
using StackExchange.Redis;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// <see cref="ITaskChangeNotifier"/> within this process: enough when the jobs run in the web
/// process (the default). Changes made by a separate processing node are not seen; the Hub's
/// polling fallback covers them.
/// </summary>
public class InMemoryTaskChangeNotifier : ITaskChangeNotifier
{
    private readonly ConcurrentDictionary<string, ImmutableList<Action>> _listeners = new();

    public virtual void Publish(string orgSlug, string dsSlug) => Dispatch(Key(orgSlug, dsSlug));

    public IDisposable Subscribe(string orgSlug, string dsSlug, Action onChange)
    {
        var key = Key(orgSlug, dsSlug);
        _listeners.AddOrUpdate(key, _ => [onChange], (_, list) => list.Add(onChange));
        return new Subscription(() =>
        {
            var left = _listeners.AddOrUpdate(key, _ => [], (_, list) => list.Remove(onChange));
            if (left.IsEmpty)
                _listeners.TryRemove(new(key, left));
        });
    }

    protected void Dispatch(string key)
    {
        if (!_listeners.TryGetValue(key, out var listeners)) return;
        foreach (var listener in listeners)
        {
            try { listener(); }
            catch { /* a listener never breaks the others */ }
        }
    }

    protected static string Key(string orgSlug, string dsSlug) => orgSlug + "/" + dsSlug;

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => System.Threading.Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}

/// <summary>
/// <see cref="ITaskChangeNotifier"/> over Redis pub/sub: changes published by any instance
/// (processing nodes included) reach the listeners of every instance.
/// </summary>
public sealed class RedisTaskChangeNotifier : InMemoryTaskChangeNotifier
{
    private readonly ISubscriber _subscriber;
    private readonly RedisChannel _channel;
    private readonly ILogger<RedisTaskChangeNotifier> _logger;

    public RedisTaskChangeNotifier(IConnectionMultiplexer redis, string instanceName, ILogger<RedisTaskChangeNotifier> logger)
    {
        _logger = logger;
        _subscriber = redis.GetSubscriber();
        _channel = RedisChannel.Literal(instanceName + "tasks-changed");
        _subscriber.Subscribe(_channel, (_, message) => Dispatch(message.ToString()));
    }

    public override void Publish(string orgSlug, string dsSlug)
    {
        try
        {
            // Every instance, this one included, dispatches when the message comes back
            _subscriber.Publish(_channel, Key(orgSlug, dsSlug), CommandFlags.FireAndForget);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cannot publish the task change of {Org}/{Ds}; dispatching locally", orgSlug, dsSlug);
            base.Publish(orgSlug, dsSlug);
        }
    }
}
