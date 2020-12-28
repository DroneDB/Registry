using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using Newtonsoft.Json;
using Registry.Common;

namespace Registry.Web.Data.Models
{
    public class Dataset
    {
        [MaxLength(128)]
        [Required]
        public string Slug { get; set; }
        public Guid InternalRef { get; set; }

        [Key]
        public int Id { get; set; }

        [Required]
        public string Name { get; set; }
        public string Description { get; set; }
        [Required]
        public DateTime CreationDate { get; set; }
        public string License { get; set; }
        public long Size { get; set; }
        public int ObjectsCount { get; set; }
        public DateTime LastEdit { get; set; }

        [Column("Meta")]
        public string MetaRaw { get; set; }
        
        public string PasswordHash { get; set; }

        [Required]
        public Organization Organization { get; set; }

        public virtual ICollection<Batch> Batches { get; set; }

        public virtual ICollection<DownloadPackage> DownloadPackages { get; set; }

        #region Meta

        [NotMapped]
        public Dictionary<string, object> Meta
        {
            get => string.IsNullOrWhiteSpace(MetaRaw)
                    ? new Dictionary<string, object>()
                    : JsonConvert.DeserializeObject<Dictionary<string, object>>(MetaRaw);

            set => MetaRaw = value == null ? null : JsonConvert.SerializeObject(value);
        }
        
        private const string PublicMetaField = "public";

        [NotMapped]
        public bool IsPublic
        {
            get => SafeGetMetaField<bool>(PublicMetaField);
            set => SafeSetMetaField(PublicMetaField, value);
        }

        private void SafeSetMetaField<T>(string field, T val)
        {
            if (!Meta.ContainsKey(field))
                Meta.Add(field, val);
            else
                Meta[field] = val;
            
        }

        private T SafeGetMetaField<T>(string field)
        {
            var res = Meta.SafeGetValue(field);
            if (!(res is T)) return default;
            return (T)res;
        }
#endregion



    }
}