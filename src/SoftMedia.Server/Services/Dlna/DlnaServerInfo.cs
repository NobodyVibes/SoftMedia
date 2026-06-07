namespace SoftMedia.Server.Services.Dlna;

/// <summary>
/// Process-stable identity for the DLNA server. The UDN (unique device name) is generated once
/// per process; on restart the TV simply re-discovers the server. (Persisting it across restarts
/// is a future refinement.) Friendly name and the enable flag are read from settings per request.
/// </summary>
public class DlnaServerInfo
{
    public string Udn { get; } = Guid.NewGuid().ToString();
}
