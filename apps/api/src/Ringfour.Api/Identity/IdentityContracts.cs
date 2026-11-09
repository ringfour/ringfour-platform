using Ringfour.Api.Persistence;

namespace Ringfour.Api.Identity;

public sealed record UserResponse(Guid Id, DateTimeOffset CreatedAt)
{
    public static UserResponse FromEntity(User user) => new(user.Id, user.CreatedAt);
}

public sealed record MembershipResponse(
    Guid OrganizationId,
    Guid UserId,
    OrgRole Role,
    DateTimeOffset CreatedAt)
{
    public static MembershipResponse FromEntity(Membership membership) => new(
        membership.OrganizationId,
        membership.UserId,
        membership.Role,
        membership.CreatedAt);
}

public sealed record MeResponse(UserResponse User, IReadOnlyList<MembershipResponse> Memberships);

public sealed record OrganizationResponse(Guid Id, string Name, DateTimeOffset CreatedAt)
{
    public static OrganizationResponse FromEntity(Organization organization) =>
        new(organization.Id, organization.Name, organization.CreatedAt);
}

public sealed record OrganizationListItem(Guid Id, string Name, OrgRole Role, DateTimeOffset CreatedAt);

public sealed record OrganizationListResponse(
    IReadOnlyList<OrganizationListItem> Items,
    string? NextCursor);

public sealed record MemberListResponse(IReadOnlyList<MembershipResponse> Items, string? NextCursor);

public sealed record CreateOrganizationRequest(string? Name);

public sealed record AddMemberRequest(Guid UserId, OrgRole Role);

public sealed record UpdateMemberRequest(OrgRole Role);
