using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Registry.Ports.DroneDB.Models;
using Registry.Web.Data.Models;
using Registry.Web.Models.DTO;

namespace Registry.Web
{
    public static class Extenders
    {
        public static Organization ToEntity(this OrganizationDto organization)
        {
            return new Organization
            {
                Id = organization.Id,
                Name = organization.Name,
                Description = organization.Description,
                CreationDate = organization.CreationDate,
                OwnerId = organization.Owner,
                IsPublic = organization.IsPublic
            };
        }

        public static OrganizationDto ToDto(this Organization organization)
        {
            return new OrganizationDto
            {
                Id = organization.Id,
                Name = organization.Name,
                Description = organization.Description,
                CreationDate = organization.CreationDate,
                Owner = organization.OwnerId,
                IsPublic = organization.IsPublic
            };
        }

        public static Dataset ToEntity(this DatasetDto organization)
        {
            return new Dataset
            {
                Id = organization.Id,
                Slug = organization.Slug,
                CreationDate = organization.CreationDate,
                Description = organization.Description,
                LastEdit = organization.LastEdit,
                Name = organization.Name,
                License = organization.License,
                Meta = organization.Meta,
                // TODO: These should be calculated
                // ObjectsCount = ObjectsCount,
                // Size = Size,
                IsPublic = organization.IsPublic
            };
        }

        public static DatasetDto ToDto(this Dataset organization)
        {
            return new DatasetDto
            {
                Id = organization.Id,
                Slug = organization.Slug,
                CreationDate = organization.CreationDate,
                Description = organization.Description,
                LastEdit = organization.LastEdit,
                Name = organization.Name,
                License = organization.License,
                Meta = organization.Meta,
                ObjectsCount = organization.ObjectsCount,
                Size = organization.Size
            };
        }

        public static ObjectDto ToDto(this DdbObject obj)
        {
            return new ObjectDto
            {
                Depth = obj.Depth,
                Hash = obj.Hash,
                Id = obj.Id,
                Meta = obj.Meta,
                ModifiedTime = obj.ModifiedTime,
                Path = obj.Path,
                PointGeometry = obj.PointGeometry,
                PolygonGeometry = obj.PolygonGeometry,
                Size = obj.Size,
                Type = (ObjectType)(int)obj.Type
            };
        }
    }
}
