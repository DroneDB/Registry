using System;
using System.IO;
using System.Threading.Tasks;

namespace Registry.Ports;

public interface IThumbnailGenerator
{
    public Task GenerateThumbnailAsync(string filePath, int size, Stream output);

}