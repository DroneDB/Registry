namespace Registry.Web.Models;

public class StorageFileDto
{
    public required string PhysicalPath { get; set; }
    public required string Name { get; set; }
    public required string ContentType { get; set; }
}