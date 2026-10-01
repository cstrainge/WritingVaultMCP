using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WritingVault.Tray;

// Task Scheduler starts this GUI-subsystem executable, so the background
// PowerShell supervisor never acquires a visible Windows Terminal window.
internal static class ServiceProcessHost
{
    internal static ProcessStartInfo CreateStartInfo(string service, string configuration, string projectRoot)
    {
        if (service is not ("viewer" or "tunnel") || configuration is not ("Debug" or "Release"))
            throw new ArgumentException("Unknown Writer's Vault service or build.");

        var script = Path.Combine(projectRoot, "tools", service == "viewer"
            ? "Run-WritingVaultWeb.ps1" : "Run-WritingVaultTunnel.ps1");
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            WorkingDirectory = projectRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Background" })
            start.ArgumentList.Add(argument);
        if (configuration == "Debug") start.ArgumentList.Add("-DebugBuild");
        return start;
    }

    internal static int Run(string service, string configuration)
    {
        var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WritingVaultMCP", "Logs", $"{service}-host-{configuration.ToLowerInvariant()}.log");
        try
        {
            using var job = new ChildProcessJob();
            using var process = new Process { StartInfo = CreateStartInfo(service, configuration,
                VaultServiceController.FindProjectRoot()) };
            if (!process.Start()) throw new InvalidOperationException("The background supervisor did not start.");
            job.Assign(process);
            // Drain both pipes throughout the service lifetime; PowerShell's
            // script reports failures here, while its child writes its own log.
            var output = DrainAsync(process.StandardOutput, logPath);
            var error = DrainAsync(process.StandardError, logPath);
            process.WaitForExit();
            Task.WaitAll(output, error);
            WriteLog(logPath, $"Supervisor stopped with exit code {process.ExitCode}.");
            return process.ExitCode;
        }
        catch (Exception exception)
        {
            WriteLog(logPath, $"Supervisor failed: {exception.Message}");
            return 1;
        }
    }

    private static async Task DrainAsync(StreamReader reader, string logPath)
    {
        while (await reader.ReadLineAsync() is { } line)
            WriteLog(logPath, line);
    }

    private static readonly object LogLock = new();
    private static void WriteLog(string path, string message)
    {
        lock (LogLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path) && new FileInfo(path).Length > 2_097_152)
                File.Move(path, path + ".1", overwrite: true);
            File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {message[..Math.Min(message.Length, 4096)]}\n");
        }
    }

    // Closing this job when Task Scheduler stops the host also closes the
    // PowerShell supervisor and its viewer/tunnel descendants.
    private sealed class ChildProcessJob : IDisposable
    {
        private const uint KillOnClose = 0x00002000;
        private readonly IntPtr handle;

        public ChildProcessJob()
        {
            handle = CreateJobObject(IntPtr.Zero, null);
            if (handle == IntPtr.Zero) throw new Win32Exception();
            var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = KillOnClose } };
            var size = Marshal.SizeOf<ExtendedLimits>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(limits, buffer, false);
                if (!SetInformationJobObject(handle, 9, buffer, (uint)size)) throw new Win32Exception();
            }
            catch { Dispose(); throw; }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        public void Assign(Process process)
        {
            if (!AssignProcessToJobObject(handle, process.Handle))
            {
                process.Kill(entireProcessTree: true);
                throw new Win32Exception();
            }
        }

        public void Dispose() => CloseHandle(handle);

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimits
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimits
        {
            public BasicLimits Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(IntPtr job, int informationClass, IntPtr information, uint length);
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr job);
    }
}
