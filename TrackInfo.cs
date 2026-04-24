namespace Mls;

public class TrackInfo
{
    public string Type = "Unknown";
    public string HandlerCode = "";
    public string Resolution = "";
    public string Codec = "";
    public bool IsDrm = false;

    public string? Language { get; internal set; }
    public int Pid { get; internal set; }
}