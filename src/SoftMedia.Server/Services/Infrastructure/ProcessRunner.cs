using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using SoftMedia.Server.Services.Abstractions;

namespace SoftMedia.Server.Services.Infrastructure;

public class ProcessRunner : IProcessRunner
{
    private readonly ILogger<ProcessRunner> _logger;

    public ProcessRunner(ILogger<ProcessRunner> logger)
    {
        _logger = logger;
    }

    public async Task<string> RunProcessAsync(ProcessStartInfo startInfo)
    {
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.CreateNoWindow = true;

        using var process = new Process { StartInfo = startInfo };
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        process.OutputDataReceived += (sender, args) =>
        {
            if (args.Data != null)
                outputBuilder.AppendLine(args.Data);
        };

        process.ErrorDataReceived += (sender, args) =>
        {
            if (args.Data != null)
                errorBuilder.AppendLine(args.Data);
        };

        try
        {
            if (!process.Start())
            {
                _logger.LogError("Failed to start process: {FileName} {Arguments}", startInfo.FileName, startInfo.Arguments);
                return string.Empty;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Wait with a reasonable timeout (e.g., 30 seconds for probe)
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(cts.Token);

            if (process.ExitCode != 0)
            {
                _logger.LogWarning("Process exited with code {Code}. Error: {Error}", process.ExitCode, errorBuilder.ToString());
            }

            return outputBuilder.ToString();
        }
        catch (OperationCanceledException)
        {
            _logger.LogError("Process timed out: {FileName} {Arguments}", startInfo.FileName, startInfo.Arguments);
            try { process.Kill(); } catch { }
            return string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error running process: {FileName}", startInfo.FileName);
            return string.Empty;
        }
    }

    public async Task<int> RunProcessForExitCodeAsync(ProcessStartInfo startInfo, TimeSpan timeout)
    {
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.CreateNoWindow = true;

        using var process = new Process { StartInfo = startInfo };
        var errorBuilder = new StringBuilder();
        process.OutputDataReceived += (_, args) => { /* drained to avoid pipe stalls */ };
        process.ErrorDataReceived += (_, args) => { if (args.Data != null) errorBuilder.AppendLine(args.Data); };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process: {startInfo.FileName}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Process timed out after {Timeout}: {FileName} {Arguments}",
                timeout, startInfo.FileName, startInfo.Arguments);
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return -1;
        }

        if (process.ExitCode != 0)
        {
            _logger.LogWarning("Process exited with code {Code}: {FileName}. Error tail: {Error}",
                process.ExitCode, startInfo.FileName,
                errorBuilder.Length > 2000 ? errorBuilder.ToString(errorBuilder.Length - 2000, 2000) : errorBuilder.ToString());
        }
        return process.ExitCode;
    }
}
