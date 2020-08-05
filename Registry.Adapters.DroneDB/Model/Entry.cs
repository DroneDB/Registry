using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text;

namespace Registry.Adapters.DroneDB.Model
{
    public class Entry
    {
        [Key]
        [Column("path", TypeName = "TEXT")]
        public string Path { get; set; }
        [Column("hash", TypeName = "TEXT")]
        public string Hash { get; set; }
        [Column("type", TypeName = "INTEGER")]
        public int Type { get; set; }
        [Column("meta", TypeName = "TEXT")]
        public string Meta { get; set; }
        [Column("mtime", TypeName = "INTEGER")]
        public DateTime ModifiedTime { get; set; }
        [Column("size", TypeName = "INTEGER")]
        public int Size { get; set; }
        [Column("depth", TypeName = "INTEGER")]
        public int Depth { get; set; }

        /*
         * path TEXT,
         * hash TEXT,
         * type INTEGER,
         * meta TEXT,
         * mtime INTEGER,
         * size  INTEGER,
         * depth INTEGER
         */
    }
}
