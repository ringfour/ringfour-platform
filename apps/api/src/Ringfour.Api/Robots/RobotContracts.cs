using Ringfour.Api.Persistence;

namespace Ringfour.Api.Robots;

public sealed record CreateRobotRequest(string? Name, string? ProfileRef);

public sealed record RobotResponse(
    Guid Id,
    Guid OrganizationId,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EnrolledAt,
    string? ProfileRef)
{
    public static RobotResponse FromEntity(Robot robot) => new(
        robot.Id,
        robot.OrganizationId,
        robot.Name,
        robot.CreatedAt,
        robot.EnrolledAt,
        robot.ProfileRef);
}

public sealed record RobotListResponse(IReadOnlyList<RobotResponse> Items, string? NextCursor);

public sealed record EnrollmentTokenResponse(
    Guid TokenId,
    Guid RobotId,
    string Token,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

public sealed record EnrollRequest(string? Token);

public sealed record EnrollResponse(
    Guid RobotId,
    Guid OrganizationId,
    Guid CredentialId,
    string CredentialSecret);

public sealed record RobotCredentialResponse(
    Guid Id,
    Guid RobotId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt)
{
    public static RobotCredentialResponse FromEntity(RobotCredential credential) => new(
        credential.Id,
        credential.RobotId,
        credential.CreatedAt,
        credential.ExpiresAt,
        credential.RevokedAt);
}

public sealed record CredentialListResponse(IReadOnlyList<RobotCredentialResponse> Items);

public sealed record RotateCredentialResponse(RobotCredentialResponse Credential, string Secret);

public sealed record DeviceAuthenticateRequest(Guid CredentialId, string? Secret);

public sealed record DeviceAuthenticateResponse(Guid RobotId, Guid OrganizationId, Guid CredentialId);
