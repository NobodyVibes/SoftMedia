using System.Diagnostics;

namespace SoftMedia.Server.Services.Abstractions;

public interface IProcessRunner
{
    Task<string> RunProcessAsync(ProcessStartInfo startInfo);
}

public class ProcessRunner : IProcessRunner
{
    public async Task<string> RunProcessAsync(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo);
        if (process == null) return string.Empty;

        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return output;
    }
}
