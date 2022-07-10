using System.IO;
using System.Reflection;

namespace Registry.Web.Services.Ports;

public interface IEmbeddedResourceQuery
{
    Stream Read<T>(string resource);
    Stream Read(Assembly assembly, string resource);
    Stream Read(string assemblyName, string resource);
    string[] GetResourceNames(Assembly assembly);
}