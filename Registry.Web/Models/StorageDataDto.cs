namespace Registry.Web.Models;

/// <summary>
/// Storage entry DTO that supports byte array data instead of file paths
/// </summary>
public class StorageDataDto
{
    public required byte[] Data { get; set; }
    public required string ContentType { get; set; }
    
    public required string Name { get; set; }
}
