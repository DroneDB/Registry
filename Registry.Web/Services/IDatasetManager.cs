using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Registry.Web.Data;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services
{
    public interface IDatasetManager
    {
        void CreateDataset(DatasetDto ds);
        void RemoveDataset(int id);

    }
}
