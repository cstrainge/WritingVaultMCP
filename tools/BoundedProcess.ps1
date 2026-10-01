Set-StrictMode -Version Latest

if (-not ('WritingVault.ProcessJob' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WritingVault
{
    public static class ProcessJob
    {
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
            public BasicLimits BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int informationClass, IntPtr information, uint length);
        [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);

        public static IntPtr CreateKillOnClose()
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) throw new Win32Exception();
            var limits = new ExtendedLimits();
            limits.BasicLimitInformation.LimitFlags = 0x00002000;
            int size = Marshal.SizeOf(typeof(ExtendedLimits));
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(limits, buffer, false);
                if (!SetInformationJobObject(job, 9, buffer, (uint)size)) throw new Win32Exception();
                return job;
            }
            catch { CloseHandle(job); throw; }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        public static void Assign(IntPtr job, IntPtr process)
        {
            if (!AssignProcessToJobObject(job, process)) throw new Win32Exception();
        }
    }
}
'@
}

function Invoke-WritingVaultBoundedProcess {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $FilePath,
        [Parameter(Mandatory)][string[]] $ArgumentList,
        [Parameter(Mandatory)][string] $LogPath,
        [string] $PidPath,
        [int64] $MaximumBytes = 2097152,
        [int] $RetainedFiles = 4
    )

    function Quote-ProcessArgument([string] $Value) {
        if ($Value.Contains('"')) { throw 'Background process arguments cannot contain quotation marks.' }
        if ($Value.Length -gt 0 -and $Value -notmatch '\s') { return $Value }
        $escaped = [regex]::Replace($Value, '(\\+)$', '$1$1')
        return '"' + $escaped + '"'
    }
    function Write-BoundedLine([string] $Line) {
        if ($null -eq $Line) { return }
        $safe = $Line
        if ($safe.Length -gt 65536) { $safe = $safe.Substring(0, 65536) + ' [truncated]' }
        $safe = [regex]::Replace($safe, '(?i)(authorization|api[_-]?key|bearer)(\s*[:=]\s*|\s+)[^\s,;]+', '$1$2[redacted]')
        if ((Test-Path -LiteralPath $LogPath -PathType Leaf) -and (Get-Item -LiteralPath $LogPath).Length -ge $MaximumBytes) {
            for ($index = $RetainedFiles - 1; $index -ge 1; $index--) {
                $source = if ($index -eq 1) { $LogPath } else { "$LogPath.$($index - 1)" }
                $target = "$LogPath.$index"
                if (Test-Path -LiteralPath $source -PathType Leaf) { Move-Item -LiteralPath $source -Destination $target -Force }
            }
        }
        Add-Content -LiteralPath $LogPath -Value $safe -Encoding UTF8
    }

    $directory = Split-Path -Parent $LogPath
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $FilePath
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    if ($start.PSObject.Properties.Name -contains 'ArgumentList') {
        foreach ($argument in $ArgumentList) { [void]$start.ArgumentList.Add($argument) }
    }
    else {
        $start.Arguments = ($ArgumentList | ForEach-Object { Quote-ProcessArgument $_ }) -join ' '
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $started = $false
    $job = [IntPtr]::Zero
    try {
        if (-not $process.Start()) { throw "Could not start $FilePath." }
        $started = $true
        $job = [WritingVault.ProcessJob]::CreateKillOnClose()
        [WritingVault.ProcessJob]::Assign($job, $process.Handle)
        if (-not [string]::IsNullOrWhiteSpace($PidPath)) {
            $pidDirectory = Split-Path -Parent $PidPath
            New-Item -ItemType Directory -Path $pidDirectory -Force | Out-Null
            $process.Id | Set-Content -LiteralPath $PidPath -Encoding Ascii -NoNewline
        }
        Write-BoundedLine ("{0:O} process.started pid={1}" -f [DateTime]::UtcNow, $process.Id)
        $stdout = $process.StandardOutput.ReadLineAsync()
        $stderr = $process.StandardError.ReadLineAsync()
        while (-not $process.HasExited -or $null -ne $stdout -or $null -ne $stderr) {
            $tasks = @()
            if ($null -ne $stdout) { $tasks += $stdout }
            if ($null -ne $stderr) { $tasks += $stderr }
            if ($tasks.Count -eq 0) { break }
            $completed = [Threading.Tasks.Task]::WhenAny($tasks).GetAwaiter().GetResult()
            if ($null -ne $stdout -and [object]::ReferenceEquals($completed, $stdout)) {
                $line = $stdout.GetAwaiter().GetResult()
                if ($null -eq $line) { $stdout = $null } else { Write-BoundedLine $line; $stdout = $process.StandardOutput.ReadLineAsync() }
            }
            elseif ($null -ne $stderr -and [object]::ReferenceEquals($completed, $stderr)) {
                $line = $stderr.GetAwaiter().GetResult()
                if ($null -eq $line) { $stderr = $null } else { Write-BoundedLine $line; $stderr = $process.StandardError.ReadLineAsync() }
            }
        }
        $process.WaitForExit()
        Write-BoundedLine ("{0:O} process.stopped exitCode={1}" -f [DateTime]::UtcNow, $process.ExitCode)
        return $process.ExitCode
    }
    finally {
        if ($started -and -not $process.HasExited) { try { $process.Kill($true) } catch { $process.Kill() } }
        if (-not [string]::IsNullOrWhiteSpace($PidPath) -and
            (Test-Path -LiteralPath $PidPath -PathType Leaf)) {
            $writtenPid = 0
            if ([int]::TryParse((Get-Content -LiteralPath $PidPath -Raw).Trim(), [ref]$writtenPid) -and
                $writtenPid -eq $process.Id) {
                Remove-Item -LiteralPath $PidPath -Force
            }
        }
        if ($job -ne [IntPtr]::Zero) { [void][WritingVault.ProcessJob]::CloseHandle($job) }
        $process.Dispose()
    }
}
