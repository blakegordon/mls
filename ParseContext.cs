namespace Mls;

public class ParseContext
{
    public long TotalFileSize;
    public string MajorBrand = "Unknown";
    public string BrandExplanation = "";
    public double DurationSeconds = 0;
    public double BitrateMbps = 0;
    public List<TrackInfo> Tracks = [];
    public TrackInfo? CurrentTrack;

    public string? DocType { get; internal set; }
    public ulong TimecodeScale { get; internal set; }
    public int PcrPid { get; internal set; } = -1;
    public ulong FirstPcr { get; internal set; }
    public int VideoPid { get; internal set; } = -1;
    public bool FoundResolution { get; internal set; }
    public ulong LastPcr { get; internal set; }
    public string? VideoCodec { get; internal set; }
    public uint TimeScale { get; internal set; }
}