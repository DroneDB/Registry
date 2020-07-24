namespace Registry.Web.Models.DTO
{
    public abstract class Dto<TEntity>
    {
        public abstract TEntity ToEntity();
    }
}