using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using Registry.Ports.DroneDB;
using Registry.Web.Data.Models;

namespace Registry.Web.Models.DTO;

public class DatasetPermissionsDto
{
    public bool CanRead { get; set; }
    public bool CanWrite { get; set; }
    public bool CanDelete { get; set; }
}

public class DatasetDto
{
        
    [Required]
    public string Slug { get; set; }
    [Required]
    public DateTime CreationDate { get; set; }
    public Dictionary<string, object> Properties { get; set; }

    public long Size { get; set; }
    
    public DatasetPermissionsDto Permissions { get; set; }

}
    
public class DatasetNewDto
{
    [Required]
    public string Slug { get; set; }

    public string Name { get; set; }
        
    public Visibility? Visibility { get; set; }

    [MaxLength(256)]
    public string Tagline { get; set; }
}
    
public class DatasetEditDto
{

    public string Name { get; set; }
        
    public Visibility? Visibility { get; set; }

}