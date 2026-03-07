namespace SoftMedia.Server.Services.Infrastructure;

public class SoftMediaUserAgentHandler : DelegatingHandler
{
    private const string UserAgent = "SoftMedia/1.0 (https://github.com/NobodyVibes/SoftMedia)";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.UserAgent.Clear();
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        return base.SendAsync(request, ct);
    }
}
