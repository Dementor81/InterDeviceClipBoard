namespace Clipboard.Server;

public sealed class AppOptions
{
    public string DataDir { get; set; } = "data";

    public long MaxUploadBytes { get; set; } = 1_073_741_824;

    public long MaxTotalBytes { get; set; } = 10_737_418_240;

    public int AbandonedAfterDays { get; set; } = 30;
}
