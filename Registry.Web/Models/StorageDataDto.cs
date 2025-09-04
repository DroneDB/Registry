using System.IO;

namespace Registry.Web.Models;

/// <summary>
/// Storage entry DTO that supports byte array data instead of file paths
/// </summary>
public class StorageDataDto
{
    public byte[] Data { get; set; }
    public string ContentType { get; set; }
    
    public string Name { get; set; }
}
