using System;
using System.Threading.Tasks;

namespace Registry.Ports;

public interface ICacheManager
{
    void Register(string seed, Func<object[], byte[]> getData, TimeSpan? expiration = null);
    void Unregister(string seed);
    Task<string> Get(string seed, string category, params object[] parameters);

    void Set(string seed, string category, string data, params object[] parameters);

    void Clear(string seed, string category = null);
    void Remove(string seed, string category, params object[] parameters);

    bool IsRegistered(string seed);
    bool IsCached(string seed, string category, params object[] parameters);
}