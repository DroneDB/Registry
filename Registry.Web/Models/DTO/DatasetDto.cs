using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using Registry.Web.Data.Models;

namespace Registry.Web.Models.DTO
{
    public class DatasetDto : Dto<Dataset>
    {
        public int Id { get; set; }
        [Required]
        public string Slug { get; set; }
        [Required]
        public string Name { get; set; }
        public string Description { get; set; }
        public DateTime CreationDate { get; set; }
        public string License { get; set; }
        public int Size { get; set; }
        public int ObjectsCount { get; set; }
        public DateTime LastEdit { get; set; }
        public string Meta { get; set; }
        public bool IsPublic { get; set; }

        public DatasetDto()
        {
            //
        }

        public DatasetDto(Dataset ds)
        {
            Id = ds.Id;
            Slug = ds.Slug;
            CreationDate = ds.CreationDate;
            Description = ds.Description;
            LastEdit = ds.LastEdit;
            Name = ds.Name;
            License = ds.License;
            Meta = ds.Meta;
            ObjectsCount = ds.ObjectsCount;
            Size = ds.Size;
        }

        public override Dataset ToEntity()
        {
            return new Dataset
            {
                Id = Id,
                Slug = Slug,
                CreationDate = CreationDate,
                Description = Description,
                LastEdit = LastEdit,
                Name = Name,
                License = License,
                Meta = Meta,
                // ObjectsCount = ObjectsCount,
                // Size = Size,
                IsPublic = IsPublic
            };
        }
    }
}
