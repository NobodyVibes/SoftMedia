using System.Diagnostics;

namespace SoftMedia.Server.Services.Abstractions;

public interface IProcessRunner
{
    Task<string> RunProcessAsync(ProcessStartInfo startInfo);

    /// <summary>
    /// R-WI-012 — run a process and report its EXIT CODE, with a caller-chosen timeout.
    /// <see cref="RunProcessAsync"/> hides the exit code and hard-kills at 30s, which is fine
    /// for quick probes but poisonous for work whose duration scales with media size (subtitle
    /// extraction over a 40GB remux): a kill mid-write left a plausible-looking partial output
    /// that "succeeded". Returns the process exit code, or -1 when the timeout elapsed (the
    /// process is killed). Throws only for start failures.
    /// </summary>
    Task<int> RunProcessForExitCodeAsync(ProcessStartInfo startInfo, TimeSpan timeout);
}


