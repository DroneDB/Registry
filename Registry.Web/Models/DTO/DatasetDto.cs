using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Registry.Web.Models.DTO
{
    public class DatasetDto
    {
        public int Id { get; set; }
        public string Slug { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public DateTime CreationDate { get; set; }
        public string License { get; set; }
        public int Size { get; set; }
        public int ObjectsCount { get; set; }
        public DateTime LastEdit { get; set; }
        public string Meta { get; set; }

    }
}
