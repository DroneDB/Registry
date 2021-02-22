using System;
using System.Collections.Generic;
using System.Text;

namespace Registry.Common
{
    // NOTE: I know I will regret this
    public enum EntryType
    {
        Undefined = 0,
        Directory = 1,
        Generic = 2,
        GeoImage = 3,
        GeoRaster = 4,
        PointCloud = 5,
        Image = 6,
        DroneDb = 7,
        Markdown = 8,
        Video = 9,
        Geovideo = 10
    }
}
