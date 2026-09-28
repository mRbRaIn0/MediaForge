namespace MediaForge.Models;

public sealed class MediaInfo
{
    public double Duration { get; init; }
    public long? Size { get; init; }
    public string VideoCodec { get; init; } = "-";
    public string AudioCodec { get; init; } = "-";
    public int Width { get; init; }
    public int Height { get; init; }
    public double Fps { get; init; }
    public string PixelFormat { get; init; } = "yuv420p";
    public string FormatName { get; init; } = string.Empty;
    public bool HasVideo { get; init; }
    public bool HasAudio { get; init; }
    public bool NeedsFix { get; init; }
    public bool Ok { get; init; } = true;
}
