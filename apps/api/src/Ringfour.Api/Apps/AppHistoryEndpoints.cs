using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Ringfour.Api.Errors;
using Ringfour.Api.Identity;
using Ringfour.Api.Persistence;

namespace Ringfour.Api.Apps;

public static class AppHistoryEndpoints
{
    public static void MapAppHistoryEndpoints(this IEndpointRouteBuilder routes) => routes.MapGet("/v1/organizations/{orgId:guid}/app-logbook", History).RequireAuthorization().WithMetadata(new AppErrorResponse());
    internal static void Event(RingfourDbContext db, AppRun run, string type, DateTimeOffset occurred, DateTimeOffset received)
    {
        var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"app-run:{run.Id}:{type}")).AsSpan(0,16));
        db.LogbookEvents.Add(new LogbookEvent { Id=id,RobotId=run.RobotId,AppOperationId=run.StartOperationId,AppInstallationId=run.InstallationId,ExecutionId=run.Id,ActorUserId=run.RequestedByUserId,
            Type=type,SchemaVersion=3,OccurredAt=occurred,ReceivedAt=received,Payload=new JsonObject { ["state"]=run.State,["image"]=JsonNode.Parse(run.Snapshot)!["image"]!.DeepClone() }.ToJsonString() });
    }
    private static async Task<IResult> History(Guid orgId,Guid? robot_id,Guid? run_id,Guid? installation_id,Guid? app_id,string? cursor,int? limit,HttpContext c,RingfourDbContext db,CancellationToken ct)
    {
        var user=await IdentityAccess.ResolveOrCreateUser(db,IdentityAccess.RequireSubject(c),ct);await IdentityAccess.RequireMembership(db,orgId,user.Id,OrgRole.Operator,ct);
        if(limit is <1 or >100 || cursor?.Length>1024) throw ApiException.InvalidInput("Invalid page limit/cursor.");
        var (at,id)=Pagination.DecodeCursor(cursor);var size=limit??100;
        var query=from e in db.LogbookEvents.AsNoTracking()
                  join r in db.AppRuns on e.ExecutionId equals (Guid?)r.Id into runs
                  from r in runs.DefaultIfEmpty()
                  join i in db.AppInstallations on (r!=null?(Guid?)r.InstallationId:e.AppInstallationId) equals (Guid?)i.Id into installations
                  from i in installations.DefaultIfEmpty()
                  where e.Robot.OrganizationId==orgId && (r!=null || e.AppInstallationId!=null || e.ExecutionId==null)
                  select new { Event=e,Run=r,Installation=i };
        if(robot_id is not null)query=query.Where(x=>x.Event.RobotId==robot_id);
        if(run_id is not null)query=query.Where(x=>x.Run!=null&&x.Run.Id==run_id);
        if(installation_id is not null)query=query.Where(x=>x.Installation!=null&&x.Installation.Id==installation_id);
        if(app_id is not null)query=query.Where(x=>x.Installation!=null&&x.Installation.AppId==app_id);
        if(at is not null)query=query.Where(x=>x.Event.ReceivedAt>at||(x.Event.ReceivedAt==at&&x.Event.Id.CompareTo(id!.Value)>0));
        var rows=await query.OrderBy(x=>x.Event.ReceivedAt).ThenBy(x=>x.Event.Id).Take(size+1).ToListAsync(ct);var page=rows.Take(size).ToList();
        return Results.Json(new { items=page.Select(x=>new { event_id=x.Event.Id,organization_id=orgId,robot_id=x.Event.RobotId,run_id=x.Run?.Id,installation_id=x.Installation?.Id,app_id=x.Installation?.AppId,
            revision_id=x.Run is null?(x.Event.Payload is null?null:JsonNode.Parse(x.Event.Payload)?["revision_id"]?.GetValue<string>()):JsonNode.Parse(x.Run.Snapshot)!["revision_id"]!.GetValue<string>(),operation_id=x.Event.AppOperationId??x.Event.OperationId,actor_user_id=x.Event.ActorUserId,
            local_sequence=x.Event.LocalSequence,type=x.Event.Type=="mission.logbook.entry"?"app.logbook.entry":x.Event.Type,schema_version=3,occurred_at=AppProtocol.Timestamp(x.Event.OccurredAt),received_at=AppProtocol.Timestamp(x.Event.ReceivedAt),payload=x.Event.Payload is null?new JsonObject { ["state"]=x.Event.State }:JsonNode.Parse(x.Event.Payload) }),
            next_cursor=Pagination.NextCursor(rows.Count>size,page,x=>x.Event.ReceivedAt,x=>x.Event.Id) });
    }
}
