using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Text;

namespace Registry.Common
{
    public class PackageVersion
    {
        protected bool Equals(PackageVersion other)
        {
            return this == other;
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Major, Minor, Build);
        }

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

        public PackageVersion(string version)
        {
            var segments = version.Split('.');
            Major = int.Parse(segments[0]);
            Minor = int.Parse(segments[1]);
            Build = int.Parse(segments[2]);
        }

        public static bool operator ==(PackageVersion p1, PackageVersion p2)
        {
            return p1?.Major == p2?.Major && p1?.Minor == p2?.Minor && p1?.Build == p2?.Build;
            
        }

        public static bool operator !=(PackageVersion p1, PackageVersion p2)
        {
            return !(p1 == p2);
        }

        public override bool Equals(object obj)
        {

            var p = obj as PackageVersion;
            
            return this == p;
        }

        public static bool operator >(PackageVersion p1, PackageVersion p2)
        {
            if (p1.Major > p2.Major)
                return true;
            if (p1.Major < p2.Major)
                return false;

            if (p1.Minor > p2.Minor)
                return true;
            if (p1.Minor < p2.Minor)
                return false;

            if (p1.Build > p2.Build)
                return true;
            if (p1.Build < p2.Build)
                return false;

            return false;
        }

        public static bool operator <(PackageVersion p1, PackageVersion p2)
        {
            if (p1.Major < p2.Major)
                return true;
            if (p1.Major > p2.Major)
                return false;

            if (p1.Minor < p2.Minor)
                return true;
            if (p1.Minor > p2.Minor)
                return false;

            if (p1.Build < p2.Build)
                return true;
            if (p1.Build > p2.Build)
                return false;

            return false;
        }

        public static bool operator >=(PackageVersion p1, PackageVersion p2)
        {
            if (p1.Major > p2.Major)
                return true;
            if (p1.Major < p2.Major)
                return false;

            if (p1.Minor > p2.Minor)
                return true;
            if (p1.Minor < p2.Minor)
                return false;

            if (p1.Build > p2.Build)
                return true;
            if (p1.Build < p2.Build)
                return false;

            return true;
        }

        public static bool operator <=(PackageVersion p1, PackageVersion p2)
        {
            if (p1.Major < p2.Major)
                return true;
            if (p1.Major > p2.Major)
                return false;

            if (p1.Minor < p2.Minor)
                return true;
            if (p1.Minor > p2.Minor)
                return false;

            if (p1.Build < p2.Build)
                return true;
            if (p1.Build > p2.Build)
                return false;

            return true;
        }

        public int Major { get; set; }
        public int Minor { get; set; }
        public int Build { get; set; }

        public override string ToString()
        {
            return $"{Major}.{Minor}.{Build}";
        }
    }
}
