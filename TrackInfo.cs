namespace Mls;

public class TrackInfo
{
    public string Type = "Unknown";
    public string HandlerCode = "";
    public string Resolution = "";
    public string Codec = "";
    public bool IsDrm = false;

    /// <summary>Channel count from AudioSampleEntry when present (still available under DRM).</summary>
    public int Channels { get; internal set; }

    /// <summary>Human layout such as 2.0 / 5.1 when Channels is known.</summary>
    public string? ChannelLayout { get; internal set; }

    public string? Language { get; internal set; }
    public int Pid { get; internal set; }
}