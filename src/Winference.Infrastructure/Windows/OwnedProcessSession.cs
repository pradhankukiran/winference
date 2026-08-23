using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Winference.Infrastructure.Windows;

/// <summary>A process is suspended until assigned to our non-inheritable kill-on-close job.
/// The job contains native descendants even when their supervisor exits first.</summary>
public sealed class OwnedProcessSession : IDisposable
{
    private readonly SafeFileHandle _job;
    private readonly SafeFileHandle _process;
    private readonly StreamReader _output;
    private readonly StreamReader _error;
    private readonly Queue<string> _diagnostics = new();
    private readonly Task[] _readers;
    private bool _disposed;

    private OwnedProcessSession(SafeFileHandle job, SafeFileHandle process, uint pid,
        SafeFileHandle output, SafeFileHandle error)
    {
        _job = job; _process = process; ProcessId = (int)pid;
        using var child = Process.GetProcessById(ProcessId);
        StartedUtc = child.StartTime.ToUniversalTime();
        _output = new StreamReader(new FileStream(output, FileAccess.Read));
        _error = new StreamReader(new FileStream(error, FileAccess.Read));
        _readers = [DrainAsync(_output), DrainAsync(_error)];
    }

    public int ProcessId { get; }
    public DateTime StartedUtc { get; }
    public bool HasExited => WaitForSingleObject(_process, 0) == 0;
    /// <summary>The exit code, read through this session's own handle so it survives the process ending.</summary>
    public int? ExitCode => HasExited && GetExitCodeProcess(_process, out var code) ? unchecked((int)code) : null;
    public string[] Diagnostics { get { lock (_diagnostics) return _diagnostics.ToArray(); } }

    public static OwnedProcessSession Start(string executable, IReadOnlyList<string> arguments,
        string workingDirectory, IReadOnlyDictionary<string, string> environment)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows is required.");
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception();
        SafeFileHandle? processHandle = null;
        SafeFileHandle? threadHandle = null;
        SafeFileHandle? outRead = null, outWrite = null, errRead = null, errWrite = null, input = null;
        IntPtr environmentBlock = IntPtr.Zero;
        try
        {
            var info = new JobExtendedLimitInformation();
            info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(job, 9, ref info, (uint)Marshal.SizeOf<JobExtendedLimitInformation>()))
                throw new Win32Exception();
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
            if (!CreatePipe(out outRead, out outWrite, ref attributes, 0) ||
                !CreatePipe(out errRead, out errWrite, ref attributes, 0)) throw new Win32Exception();
            if (!SetHandleInformation(outRead, 1, 0) || !SetHandleInformation(errRead, 1, 0)) throw new Win32Exception();
            input = CreateFileW("NUL", 0x80000000, 3, ref attributes, 3, 0, IntPtr.Zero);
            if (input.IsInvalid) throw new Win32Exception();
            var startup = new StartupInfo
            {
                cb = Marshal.SizeOf<StartupInfo>(), dwFlags = 0x100,
                hStdInput = input.DangerousGetHandle(), hStdOutput = outWrite.DangerousGetHandle(),
                hStdError = errWrite.DangerousGetHandle()
            };
            var block = string.Join('\0', environment.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(x => $"{x.Key}={x.Value}")) + "\0\0";
            environmentBlock = Marshal.StringToHGlobalUni(block);
            var commandLine = new StringBuilder(string.Join(' ', new[] { executable }.Concat(arguments).Select(Quote)));
            if (!CreateProcessW(executable, commandLine, IntPtr.Zero, IntPtr.Zero, true,
                    0x4 | 0x400 | 0x08000000, environmentBlock, workingDirectory, ref startup, out var process))
                throw new Win32Exception();
            processHandle = new SafeFileHandle(process.hProcess, true);
            threadHandle = new SafeFileHandle(process.hThread, true);
            if (!AssignProcessToJobObject(job, processHandle)) throw new Win32Exception();
            if (ResumeThread(threadHandle) == uint.MaxValue) throw new Win32Exception();
            var result = new OwnedProcessSession(job, processHandle, process.dwProcessId, outRead, errRead);
            processHandle = null; outRead = null; errRead = null;
            return result;
        }
        catch
        {
            // This handle is ours, including when job assignment fails before resume.
            if (processHandle is { IsInvalid: false }) TerminateProcess(processHandle, 1);
            job.Dispose();
            throw;
        }
        finally
        {
            if (environmentBlock != IntPtr.Zero) Marshal.FreeHGlobal(environmentBlock);
            threadHandle?.Dispose(); processHandle?.Dispose(); outRead?.Dispose(); errRead?.Dispose();
            outWrite?.Dispose(); errWrite?.Dispose(); input?.Dispose();
        }
    }
}
