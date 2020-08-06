using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using NetTopologySuite.Geometries;

namespace Registry.Adapters.DroneDB.Models
{

    /*
     *CREATE TABLE entries (
      path TEXT,
      hash TEXT,
      type INTEGER,
      meta TEXT,
      mtime INTEGER,
      size  INTEGER,
      depth INTEGER
  , "point_geom" POINT, "polygon_geom" POLYGON)
     *
     */

    public class Entry
    {
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

        [Column("point_geom", TypeName = "POINT")]
        public Point PointGeometry { get; set; }

        [Column("polygon_geom", TypeName = "POLYGON")]
        public Polygon PolygonGeometry { get; set; }

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
