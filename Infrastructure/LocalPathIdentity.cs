using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace WritingVaultMcp.Infrastructure;

internal static class LocalPathIdentity
{
    private const int InitialBufferSize = 512;

    public static string Canonicalize(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows()) return fullPath;

        var volumePath = CallWithGrowingBuffer(
            (buffer, capacity) => GetVolumePathName(fullPath, buffer, capacity),
            "resolve the local volume mount point");
        var volumeName = CallWithGrowingBuffer(
            (buffer, capacity) => GetVolumeNameForVolumeMountPoint(volumePath, buffer, capacity),
            "resolve the local volume identity");
        var relative = Path.GetRelativePath(volumePath, fullPath);
        return relative == "."
            ? volumeName.TrimEnd(Path.DirectorySeparatorChar)
            : Path.Combine(volumeName, relative);
    }

    private static string CallWithGrowingBuffer(Func<StringBuilder, int, bool> call, string operation)
    {
        var capacity = InitialBufferSize;
        while (capacity <= 32_768)
        {
            var buffer = new StringBuilder(capacity);
            if (call(buffer, capacity)) return buffer.ToString();
            var error = Marshal.GetLastWin32Error();
            if (error != 122) throw new IOException($"Could not {operation}.", new Win32Exception(error));
            capacity *= 2;
        }
        throw new IOException($"Could not {operation}: the resolved path was too long.");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(
        string fileName,
        StringBuilder volumePathName,
        int bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(
        string volumeMountPoint,
        StringBuilder volumeName,
        int bufferLength);
}
