using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using GeoJSON.Net.Geometry;
using Registry.Common;

namespace Registry.Adapters.DroneDB.Models
{

    public class Entry
    {
        public string Path { get; set; }
        public string Hash { get; set; }
        public EntryType Type { get; set; }
        public string Meta { get; set; }

        public DateTime ModifiedTime { get; set; }

        public int Size { get; set; }
        public int Depth { get; set; }

        public Point PointGeometry { get; set; }

        public Polygon PolygonGeometry { get; set; }

        
    }

}
