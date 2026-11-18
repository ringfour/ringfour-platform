namespace Ringfour.Api.Persistence;

// Role hierarchy: Owner includes Developer rights, Developer includes Operator rights.
public enum OrgRole
{
    Owner = 3,
    Developer = 2,
    Operator = 1,
}

public sealed class User
{
    public Guid Id { get; set; }
    public required string Auth0Sub { get; set; }
    public string? Name { get; set; }
    public string? Email { get; set; }
    public string? ProfilePictureUrl { get; set; }
    public string? PhoneNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public List<Membership> Memberships { get; } = [];
}

public sealed class Organization
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public List<Membership> Memberships { get; } = [];
}

public sealed class Membership
{
    public Guid OrganizationId { get; set; }
    public Guid UserId { get; set; }
    public OrgRole Role { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public Organization Organization { get; set; } = null!;
    public User User { get; set; } = null!;
}

public sealed class OrganizationInvitation
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public required string Email { get; set; }
    public required string NormalizedEmail { get; set; }
    public OrgRole Role { get; set; }
    public Guid InvitedByUserId { get; set; }
    public required string TokenHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public Guid? AcceptedByUserId { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    public Organization Organization { get; set; } = null!;
    public User InvitedBy { get; set; } = null!;
    public User? AcceptedBy { get; set; }
}
