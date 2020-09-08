using System;
using System.Collections.Generic;
using System.Text;
using System.Runtime.InteropServices;

namespace Registry.Common
{

    public static class OperatingSystemInfo
    {
        public static bool IsWindows() =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        public static bool IsMacOS() =>
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        public static bool IsLinux() =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

        public static string PlatformName => IsWindows() ? "windows" : IsLinux() ? "linux" : IsMacOS() ? "macosx" : "unknown";
    }
}
