namespace DataPerformanceLab.Data;

public sealed class LabMetadataEntry
{
    public string MetadataKey { get; set; } = string.Empty;

    public string MetadataValue { get; set; } = string.Empty;

    public DateTime UpdatedAtUtc { get; set; }
}
