using System.Runtime.InteropServices;
using Registry.Common;

namespace Registry.Test.Common;

/// <summary>Makes the native libddb loadable by the tests that call it.</summary>
public static class DdbLibrary
{
    /// <summary>
    /// On Windows, adds the folder of the PATH that holds libddb to the DLL search path (a P/Invoke does not look
    /// there by itself). Other platforms find it through their loader path: nothing to do.
    /// </summary>
    public static void UseFromPath()
    {
        if (!OperatingSystem.IsWindows())
            return;

        const uint LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x00001000;

        var ddbFolder = CommonUtils.FindDdbFolder() ?? throw new InvalidOperationException("DDB not found");

        if (!SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

        if (AddDllDirectory(ddbFolder) == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr AddDllDirectory([MarshalAs(UnmanagedType.LPWStr)] string lpPathName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetDefaultDllDirectories(uint flags);
}
