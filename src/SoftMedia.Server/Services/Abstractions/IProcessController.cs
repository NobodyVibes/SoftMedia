namespace SoftMedia.Server.Services.Abstractions;

using System.Diagnostics;

/// <summary>
/// Cross-platform interface for suspending and resuming processes.
/// Used by the throttle system to pause FFmpeg when buffer is full.
/// </summary>
public interface IProcessController
{
    /// <summary>
    /// Suspend a running process. On Windows uses NtSuspendProcess,
    /// on Unix sends SIGSTOP signal.
    /// </summary>
    /// <param name="process">The process to suspend</param>
    /// <returns>True if suspension succeeded</returns>
    bool Suspend(Process process);
    
    /// <summary>
    /// Resume a suspended process. On Windows uses NtResumeProcess,
    /// on Unix sends SIGCONT signal.
    /// </summary>
    /// <param name="process">The process to resume</param>
    /// <returns>True if resume succeeded</returns>
    bool Resume(Process process);
}
