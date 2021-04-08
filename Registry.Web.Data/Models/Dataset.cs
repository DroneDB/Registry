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
        public long Size { get; set; }
        public int ObjectsCount { get; set; }
        public DateTime LastEdit { get; set; }

        public string PasswordHash { get; set; }

        [Required]
        public Organization Organization { get; set; }

        public virtual ICollection<Batch> Batches { get; set; }

        public virtual ICollection<DownloadPackage> DownloadPackages { get; set; }

        #region Meta

        [Column("Meta")]
        public string MetaRaw
        {
            get => JsonConvert.SerializeObject(Meta);
            set => Meta = JsonConvert.DeserializeObject<Dictionary<string, object>>(value);
        }

        [NotMapped]
        public Dictionary<string, object> Meta { get; set; }

        private const string LastUpdateField = "mtime";
        private const string PublicMetaField = "public";

        [NotMapped]
        public bool IsPublic
        {
            get => SafeGetMetaField<bool>(PublicMetaField);
            set => SafeSetMetaField(PublicMetaField, value);
        }

        [NotMapped]
        public DateTime? LastUpdate
        {
            get
            {
                var val = SafeGetMetaField<long?>(LastUpdateField);

                if (val == null) return null;

                var dateTimeOffset = DateTimeOffset.FromUnixTimeSeconds(val.Value);

                return dateTimeOffset.LocalDateTime;

            }
            set
            {
                if (value == null) { 
                    SafeSetMetaField<long?>(LastUpdateField, null);
                    return;
                }

                var dt = new DateTimeOffset(value.Value);
                
                SafeSetMetaField(LastUpdateField, dt.ToUnixTimeSeconds());
            }
        }

        private void SafeSetMetaField<T>(string field, T val)
        {
            if (Meta == null)
            {
                Meta = new Dictionary<string, object>
                {
                    { field, val }
                };
                return;
            }

            if (Meta.ContainsKey(field))
                Meta[field] = val;
            else
                Meta.Add(field, val);
        }

        private T SafeGetMetaField<T>(string field)
        {
            var res = Meta?.SafeGetValue(field);
            if (!(res is T)) return default;
            return (T)res;
        }
        #endregion



    }
}