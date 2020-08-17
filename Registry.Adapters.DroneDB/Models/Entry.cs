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
        public EntryType Type { get; set; }
        [Column("meta", TypeName = "TEXT")]
        public string Meta { get; set; }

        [Column("mtime", TypeName = "INTEGER")]
        public DateTime ModifiedTime { get; set; }

        [Column("size", TypeName = "INTEGER")]
        public int Size { get; set; }
        [Column("depth", TypeName = "INTEGER")]
        public int Depth { get; set; }

        [Column("point_geom", TypeName = "POINTZ")]
        public Point PointGeometry { get; set; }

        [Column("polygon_geom", TypeName = "POLYGONZ")]
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

    public enum EntryType
    {
        Undefined = 0,
        Directory = 1,
        Generic = 2,
        GeoImage = 3,
        GeoRaster = 4,
        PointCloud = 5,
        Image = 6,
        DroneDb = 7
    }
}
