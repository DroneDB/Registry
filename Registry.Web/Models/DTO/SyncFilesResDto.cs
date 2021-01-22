namespace Registry.Web.Models.DTO
{
    public class SyncFilesResDto
    {
        public string[] SyncedFiles { get; set; }
        public SyncFileErrorDto[] ErrorFiles { get; set; }
    }
}