namespace Ringfour.Api.Persistence;

public sealed class ScriptRevision
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid CreatedByUserId { get; set; }
    public required string State { get; set; }
    public required string ArchiveSha256 { get; set; }
    public int ArchiveSize { get; set; }
    public required string RevisionDigest { get; set; }
    public required string Entrypoint { get; set; }
    public required string PythonRequirement { get; set; }
    public required string LockSha256 { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string RequestFingerprint { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UploadExpiresAt { get; set; }
    public DateTimeOffset? FinalizedAt { get; set; }
    public DateTimeOffset? UploadCleanedAt { get; set; }
    public Organization Organization { get; set; } = null!;
    public User CreatedBy { get; set; } = null!;
}
