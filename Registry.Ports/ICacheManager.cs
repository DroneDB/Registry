using System;
using System.Threading.Tasks;

namespace Registry.Ports
{
    public interface ICacheManager
    {
        void Register(string seed, Func<object[], byte[]> getData, TimeSpan? expiration = null);
        void Unregister(string seed);
        Task<string> Get(string seed, string category, params object[] parameters);
        Task Clear(string seed, string category = null);
        void Remove(string seed, string category, params object[] parameters);
    }
}
