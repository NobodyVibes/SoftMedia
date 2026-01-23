using System.Diagnostics;

namespace SoftMedia.Server.Services.Abstractions;

public interface IProcessRunner
{
    Task<string> RunProcessAsync(ProcessStartInfo startInfo);
}


