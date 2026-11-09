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
