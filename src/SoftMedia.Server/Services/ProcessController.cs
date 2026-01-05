using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SoftMedia.Server.Services.Abstractions;

namespace SoftMedia.Server.Services;

/// <summary>
/// Cross-platform process controller for suspending and resuming FFmpeg processes.
/// Uses NtSuspendProcess/NtResumeProcess on Windows and SIGSTOP/SIGCONT on Unix.
/// </summary>
public class ProcessController : IProcessController
{
    private readonly ILogger<ProcessController> _logger;

    public ProcessController(ILogger<ProcessController> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public bool Suspend(Process process)
    {
        if (process == null || process.HasExited)
        {
            _logger.LogDebug("Cannot suspend: process is null or has exited");
            return false;
        }
        
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return SuspendWindows(process);
            }
            else
            {
                return SuspendUnix(process);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to suspend process {Pid}", process.Id);
            return false;
        }
    }

    /// <inheritdoc />
    public bool Resume(Process process)
    {
        if (process == null || process.HasExited)
        {
            _logger.LogDebug("Cannot resume: process is null or has exited");
            return false;
        }
        
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return ResumeWindows(process);
            }
            else
            {
                return ResumeUnix(process);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resume process {Pid}", process.Id);
            return false;
        }
    }

    #region Windows Implementation
    
    // NT status code for success
    private const int STATUS_SUCCESS = 0;
    
    [SupportedOSPlatform("windows")]
    [DllImport("ntdll.dll", SetLastError = true)]
    private static extern int NtSuspendProcess(IntPtr processHandle);
    
    [SupportedOSPlatform("windows")]
    [DllImport("ntdll.dll", SetLastError = true)]
    private static extern int NtResumeProcess(IntPtr processHandle);

    [SupportedOSPlatform("windows")]
    private bool SuspendWindows(Process process)
    {
        var result = NtSuspendProcess(process.Handle);
        if (result == STATUS_SUCCESS)
        {
            _logger.LogDebug("Suspended Windows process {Pid}", process.Id);
            return true;
        }
        
        _logger.LogWarning("NtSuspendProcess returned {Result} for pid {Pid}", result, process.Id);
        return false;
    }

    [SupportedOSPlatform("windows")]
    private bool ResumeWindows(Process process)
    {
        var result = NtResumeProcess(process.Handle);
        if (result == STATUS_SUCCESS)
        {
            _logger.LogDebug("Resumed Windows process {Pid}", process.Id);
            return true;
        }
        
        _logger.LogWarning("NtResumeProcess returned {Result} for pid {Pid}", result, process.Id);
        return false;
    }

    #endregion

    #region Unix Implementation (Linux/macOS)

    private bool SuspendUnix(Process process)
    {
        var success = SendSignal(process.Id, "STOP");
        if (success)
        {
            _logger.LogDebug("Sent SIGSTOP to Unix process {Pid}", process.Id);
        }
        return success;
    }

    private bool ResumeUnix(Process process)
    {
        var success = SendSignal(process.Id, "CONT");
        if (success)
        {
            _logger.LogDebug("Sent SIGCONT to Unix process {Pid}", process.Id);
        }
        return success;
    }

    /// <summary>
    /// Send a signal to a process using the kill command.
    /// </summary>
    private bool SendSignal(int pid, string signal)
    {
        try
        {
            var killProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "kill",
                    Arguments = $"-{signal} {pid}",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };
            
            killProcess.Start();
            killProcess.WaitForExit(2000);
            
            if (killProcess.ExitCode != 0)
            {
                var error = killProcess.StandardError.ReadToEnd();
                _logger.LogWarning("kill -{Signal} {Pid} failed: {Error}", signal, pid, error);
                return false;
            }
            
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send {Signal} to process {Pid}", signal, pid);
            return false;
        }
    }

    #endregion
}
