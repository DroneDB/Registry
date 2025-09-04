using System;
using System.Threading.Tasks;

namespace Registry.Ports;

public interface ICacheManager
{
    void Register(string seed, Func<object[], Task<byte[]>> getData, TimeSpan? expiration = null);
    void Unregister(string seed);
    Task<byte[]> GetAsync(string seed, string category, params object[] parameters);

    Task SetAsync(string seed, string category, byte[] data, params object[] parameters);

    Task RemoveAsync(string seed, string category, params object[] parameters);

    bool IsRegistered(string seed);
}