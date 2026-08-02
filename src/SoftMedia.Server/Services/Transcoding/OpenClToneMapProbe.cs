using System.Diagnostics;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Infrastructure;

namespace SoftMedia.Server.Services.Transcoding;

/// <summary>
/// QS-WI-012 — answers "can this server's ffmpeg + GPU driver stack actually run the OpenCL
/// tone-map pipeline?" once per server run. The bundled ffmpeg ships the OpenCL filters, but
/// the pipeline also needs a working OpenCL runtime (normally installed with Intel/AMD GPU
/// drivers) — on a box without one, `-init_hw_device opencl` fails and the whole transcode
/// would die. The profile builder therefore consults this probe and falls back to the
/// software zscale/tonemap chain (the universal fallback that is never removed) when the
/// answer is no. The probe encodes one tiny synthetic HDR frame through the exact
/// hwupload → tonemap_opencl → hwdownload chain the real pipeline uses.
/// </summary>
public interface IOpenClToneMapProbe
{
    /// <summary>True when the OpenCL tone-map chain works end-to-end on this machine.
    /// Cached for the server's lifetime after the first call.</summary>
    Task<bool> IsAvailableAsync();
}

public class OpenClToneMapProbe : IOpenClToneMapProbe
{
    private readonly IBinaryLocationService _binaries;
    private readonly IProcessRunner _processRunner;
    private readonly ILogger<OpenClToneMapProbe> _logger;
    private readonly Lazy<Task<bool>> _cached;

    public OpenClToneMapProbe(
        IBinaryLocationService binaries,
        IProcessRunner processRunner,
        ILogger<OpenClToneMapProbe> logger)
    {
        _binaries = binaries;
        _processRunner = processRunner;
        _logger = logger;
        _cached = new Lazy<Task<bool>>(ProbeAsync);
    }

    public Task<bool> IsAvailableAsync() => _cached.Value;

    private async Task<bool> ProbeAsync()
    {
        try
        {
            // One 64x64 synthetic frame, tagged PQ/bt2020 (testsrc2 alone is SDR), pushed
            // through the exact filter chain BuildTranscodeArgumentsAsync emits for OpenCL.
            var startInfo = new ProcessStartInfo
            {
                FileName = _binaries.ResolveFFmpegPath(),
                Arguments =
                    "-hide_banner -v error -init_hw_device opencl=ocl -filter_hw_device ocl " +
                    "-f lavfi -i testsrc2=size=64x64:rate=10:duration=0.2 " +
                    "-vf \"setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc," +
                    "format=p010le,hwupload,tonemap_opencl=format=nv12:p=bt709:t=bt709:m=bt709:tonemap=hable," +
                    "hwdownload,format=nv12\" " +
                    "-frames:v 1 -f null -",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };

            var exitCode = await _processRunner.RunProcessForExitCodeAsync(startInfo, TimeSpan.FromSeconds(20));
            var available = exitCode == 0;
            _logger.LogInformation(
                "OpenCL tone-map probe: {Result} (ffmpeg exit code {Code}). {Consequence}",
                available ? "available" : "unavailable", exitCode,
                available
                    ? "Intel/AMD HDR tone-mapping will run on the GPU (tonemap_opencl)."
                    : "Intel/AMD HDR tone-mapping will use the software zscale/tonemap fallback.");
            return available;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenCL tone-map probe failed to run; using the software tone-map fallback.");
            return false;
        }
    }
}
