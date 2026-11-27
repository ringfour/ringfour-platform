namespace Ringfour.Api.Persistence;

public sealed class RobotAppInventory
{
    public bool RunsSupported { get; set; }
    public bool InstallationsSupported { get; set; }
    public bool UninstallSupported { get; set; }
    public bool ManifestV2Supported { get; set; }
    public bool LiveSettingsSupported { get; set; }
    public Guid RobotId { get; set; }
    public Guid DeploymentNamespace { get; set; }
    public Guid SessionId { get; set; }
    public string OfferedVersions { get; set; } = "[]";
    public bool Supported { get; set; }
    public DateTimeOffset NegotiatedAt { get; set; }
    public long ReportSequence { get; set; }
    public DateTimeOffset? ReceivedAt { get; set; }
    public string? Platform { get; set; }
    public string? Inventory { get; set; }
}

public sealed class AppDeviceReport
{
    public Guid RobotId { get; set; }
    public Guid ReportId { get; set; }
    public Guid DeploymentNamespace { get; set; }
    public string Route { get; set; } = "inventory";
    public long Sequence { get; set; }
    public string BodySha256 { get; set; } = null!;
    public string Receipt { get; set; } = null!;
}
