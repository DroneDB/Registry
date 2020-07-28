using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports
{
    public interface IDatasetManager
    {
        void CreateDataset(DatasetDto ds);
        void RemoveDataset(int id);

    }
}
