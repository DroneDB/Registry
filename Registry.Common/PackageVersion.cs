using System;
using System.Collections.Generic;
using System.Text;

namespace Registry.Common
{
    public class PackageVersion
    {

        public PackageVersion()
        {
            //
        }

        public PackageVersion(int major, int minor, int build)
        {
            Major = major;
            Minor = minor;
            Build = build;
        }

        public int Major { get; set; }
        public int Minor { get; set; }
        public int Build { get; set; }
    }
}
