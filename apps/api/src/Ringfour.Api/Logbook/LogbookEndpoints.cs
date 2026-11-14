using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ringfour.Api.Errors;
using Ringfour.Api.Persistence;
using Ringfour.Api.Robots;
using Ringfour.Api.Identity;
using Microsoft.AspNetCore.Mvc;
using Ringfour.Api.Storage;

namespace Ringfour.Api.Logbook;

public static class LogbookEndpoints
{
    private const long MaxSequence = 9007199254740991;
    private static readonly Regex Timestamp = new(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,6})?Z$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> States = ["accepted", "running", "succeeded", "failed", "cancelled"];
    private sealed record Event(Guid Id, Guid RobotId, Guid? OperationId, long Sequence, DateTimeOffset OccurredAt, string Type, string? State, Guid? ExecutionId, string? Payload);

    private static bool ValidMotionPayload(JsonElement payload, Guid eventId, DateTimeOffset occurred)
    {
        if (!Fields(payload, "event_id", "stream_id", "sequence", "occurred_at", "event_type", "operation_id", "session_id", "owner", "stop_verified") ||
            Id(payload.GetProperty("event_id")) != eventId || !payload.GetProperty("sequence").TryGetInt64(out var sequence) || sequence is < 1 or > MaxSequence ||
            payload.GetProperty("occurred_at").ValueKind != JsonValueKind.String || payload.GetProperty("occurred_at").GetString() is not { } timestamp ||
            !Timestamp.IsMatch(timestamp) || !DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var payloadTime) || payloadTime != occurred ||
            payload.GetProperty("event_type").ValueKind != JsonValueKind.String ||
            payload.GetProperty("event_type").GetString() is not ("motion.accepted" or "motion.refused" or "motion.completed" or "motion.cancelled" or "motion.failed" or "motion.stop_verified" or "motion.guard_tripped" or "motion.history_incomplete") ||
            Encoding.UTF8.GetByteCount(payload.GetRawText()) > 8192) return false;
        _ = Id(payload.GetProperty("stream_id"));
        var verified = payload.GetProperty("stop_verified");
        if (payload.GetProperty("event_type").GetString() == "motion.stop_verified" ? verified.ValueKind != JsonValueKind.True : verified.ValueKind != JsonValueKind.Null) return false;
        foreach (var name in new[] { "operation_id", "session_id", "owner" })
        {
            var value = payload.GetProperty(name);
            if (value.ValueKind == JsonValueKind.Null) continue;
            if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text || text.Contains('\0') || text.EnumerateRunes().Count() is < 1 or > 200) return false;
        }
        return true;
    }

    private static bool ValidAuthorPayload(JsonElement payload)
    {
        if (!Fields(payload, "level", "message", "fields") || payload.GetProperty("level").ValueKind != JsonValueKind.String ||
            payload.GetProperty("level").GetString() != "info" || payload.GetProperty("message").ValueKind != JsonValueKind.String ||
            payload.GetProperty("message").GetString() is not { } message || string.IsNullOrWhiteSpace(message) ||
            message.Contains('\0') || message.EnumerateRunes().Count() > 500 || payload.GetProperty("fields").ValueKind != JsonValueKind.Object ||
            Encoding.UTF8.GetByteCount(payload.GetRawText()) > 8192) return false;
        var fields = payload.GetProperty("fields").EnumerateObject().ToArray();
        if (fields.Length > 16 || fields.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count() != fields.Length) return false;
        foreach (var field in fields)
        {
            if (!Regex.IsMatch(field.Name, "^[a-zA-Z][a-zA-Z0-9_]{0,63}$", RegexOptions.CultureInvariant)) return false;
            if (field.Value.ValueKind == JsonValueKind.String && (field.Value.GetString()!.Contains('\0') || field.Value.GetString()!.EnumerateRunes().Count() > 512)) return false;
            if (field.Value.ValueKind == JsonValueKind.Number && (!field.Value.TryGetDouble(out var number) || !double.IsFinite(number) || Math.Abs(number) > 9007199254740991)) return false;
            if (field.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null)) return false;
        }
        return true;
    }

    public static void MapLogbookEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/device/logbook/events", Ingest);
        app.MapGet("/v1/organizations/{orgId:guid}/events", History).RequireAuthorization();
    }

    private static async Task<IResult> History(HttpContext context, RingfourDbContext db, Guid orgId,
        [FromQuery(Name = "robot_id")] Guid? robotId, [FromQuery(Name = "execution_id")] Guid? executionId,
        [FromQuery(Name = "operation_id")] Guid? operationId, [FromQuery(Name = "actor_user_id")] Guid? actorId,
        string? type, [FromQuery(Name = "from")] DateTimeOffset? from, [FromQuery(Name = "to")] DateTimeOffset? to,
        string? cursor, int? limit, CancellationToken ct)
    {
        var user = await IdentityAccess.ResolveOrCreateUser(db, IdentityAccess.RequireSubject(context), ct);
        await IdentityAccess.RequireMembership(db, orgId, user.Id, OrgRole.Operator, ct);
        if (limit is < 1 or > 100 || (from is not null && to is not null && from >= to) ||
            (type is not null && type is not ("mission.logbook.entry" or "device.operation.state_changed" or "agent.motion.event")))
            throw ApiException.InvalidInput("Invalid history filter.");
        from = from?.ToUniversalTime();
        to = to?.ToUniversalTime();
        var (after, id) = Pagination.DecodeCursor(cursor);
        var query = db.LogbookEvents.AsNoTracking().Include(item => item.Operation)
            .Where(item => item.Robot.OrganizationId == orgId);
        if (robotId is not null) query = query.Where(item => item.RobotId == robotId);
        if (executionId is not null) query = query.Where(item => item.ExecutionId == executionId);
        if (operationId is not null) query = query.Where(item => item.OperationId == operationId);
        if (actorId is not null) query = query.Where(item => item.Operation != null && item.Operation.RequestedByUserId == actorId);
        if (type is not null) query = query.Where(item => item.Type == type);
        if (from is not null) query = query.Where(item => item.OccurredAt >= from);
        if (to is not null) query = query.Where(item => item.OccurredAt < to);
        if (after is not null) query = query.Where(item => item.ReceivedAt > after || (item.ReceivedAt == after && item.Id.CompareTo(id!.Value) > 0));
        var pageSize = limit ?? 100;
        var rows = await query.OrderBy(item => item.ReceivedAt).ThenBy(item => item.Id).Take(pageSize + 1).ToListAsync(ct);
        var page = rows.Take(pageSize).ToList();
        return Results.Ok(new { items = page.Select(item => new { event_id = item.Id, robot_id = item.RobotId,
            local_sequence = item.LocalSequence, operation_id = item.OperationId, execution_id = item.ExecutionId,
            actor_user_id = item.Operation != null ? item.Operation.RequestedByUserId : (Guid?)null, type = item.Type, schema_version = item.SchemaVersion,
            occurred_at = item.OccurredAt, received_at = item.ReceivedAt,
            payload = item.Payload is null ? new JsonObject { ["state"] = item.State } : JsonNode.Parse(item.Payload) }),
            next_cursor = Pagination.NextCursor(rows.Count > pageSize, page, item => item.ReceivedAt, item => item.Id) });
    }

    private static bool Fields(JsonElement value, params string[] fields) =>
        value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(fields.Order());

    private static Guid Id(JsonElement element)
    {
        var value = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        if (!Guid.TryParseExact(value, "D", out var id) || id.ToString() != value) throw new JsonException();
        return id;
    }

    private static List<Event> Parse(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (!Fields(root, "events") || root.GetProperty("events").ValueKind != JsonValueKind.Array) throw new JsonException();
        var array = root.GetProperty("events");
        if (array.GetArrayLength() is < 1 or > 100) throw new JsonException();
        var events = new List<Event>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("type", out var kindValue) || kindValue.ValueKind != JsonValueKind.String) throw new JsonException();
            var kind = kindValue.GetString();
            var author = kind == "mission.logbook.entry";
            var motion = kind == "agent.motion.event";
            if (kind is not ("device.operation.state_changed" or "mission.logbook.entry" or "agent.motion.event") ||
                !(author ? Fields(item, "event_id", "robot_id", "local_sequence", "occurred_at", "type", "schema_version", "operation_id", "execution_id", "payload") :
                    Fields(item, "event_id", "robot_id", "local_sequence", "occurred_at", "type", "schema_version", "operation_id", "payload")) ||
                !item.GetProperty("schema_version").TryGetInt32(out var version) || version != 1 ||
                !item.GetProperty("local_sequence").TryGetInt64(out var sequence) || sequence is < 1 or > MaxSequence ||
                item.GetProperty("occurred_at").ValueKind != JsonValueKind.String ||
                item.GetProperty("occurred_at").GetString() is not { } timestamp || !Timestamp.IsMatch(timestamp) ||
                !DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var occurred)) throw new JsonException();
            var payload = item.GetProperty("payload");
            var eventId = Id(item.GetProperty("event_id"));
            string? state = null;
            if (author) { if (!ValidAuthorPayload(payload)) throw new JsonException(); }
            else if (motion) { if (item.GetProperty("operation_id").ValueKind != JsonValueKind.Null || !ValidMotionPayload(payload, eventId, occurred)) throw new JsonException(); }
            else if (!Fields(payload, "state") || payload.GetProperty("state").ValueKind != JsonValueKind.String ||
                (state = payload.GetProperty("state").GetString()) is null || !States.Contains(state)) throw new JsonException();
            var record = new Event(eventId, Id(item.GetProperty("robot_id")), motion ? null : Id(item.GetProperty("operation_id")), sequence, occurred,
                kind!, state, author ? Id(item.GetProperty("execution_id")) : null, author || motion ? payload.GetRawText() : null);
            if (events.Count > 0 && (events[^1].Sequence == MaxSequence || record.Sequence != events[^1].Sequence + 1))
                throw ApiException.Conflict("sequence_gap", "The batch must contain consecutive ordered sequences.");
            events.Add(record);
        }
        if (events.Select(item => item.Id).Distinct().Count() != events.Count) throw new JsonException();
        return events;
    }

    private static async Task<IResult> Ingest(HttpContext context, RingfourDbContext db, CancellationToken ct)
    {
        if (!Guid.TryParse(context.Request.Headers["x-ringfour-credential-id"].ToString(), out var credentialId))
            throw ApiException.InvalidToken("A valid robot credential is required.");
        var (robot, credential) = await DeviceAuth.VerifyCredential(db, credentialId, context.Request.Headers["x-ringfour-credential-secret"].ToString(), ct);
        List<Event> events;
        try { events = Parse(await ScriptRevisionEndpoints.ReadLimited(context.Request.Body, 65536, ct)); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        { throw ApiException.InvalidInput("Invalid logbook event batch."); }
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM robots WHERE id = {robot.Id} FOR UPDATE", ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM robot_credentials WHERE id = {credential.Id} FOR UPDATE", ct);
        await db.Entry(credential).ReloadAsync(ct);
        if (credential.RevokedAt is not null || credential.ExpiresAt <= DateTimeOffset.UtcNow)
            throw ApiException.InvalidToken("The robot credential is invalid, expired, or revoked.");
        var checkpoint = await db.LogbookCheckpoints.SingleOrDefaultAsync(item => item.RobotId == robot.Id, ct);
        long sequence = checkpoint?.Sequence ?? 0;
        foreach (var item in events)
        {
            if (item.RobotId != robot.Id || (item.OperationId is Guid operation && !await db.Operations.AnyAsync(op => op.Id == operation && op.RobotId == robot.Id, ct)))
                throw ApiException.Conflict("event_conflict", "Event robot or operation does not match the credential.");
            if (item.ExecutionId is Guid execution && !await db.ScriptExecutions.AnyAsync(e => e.Id == execution && e.RobotId == robot.Id && e.Id == item.OperationId, ct) &&
                !await db.DevSessionRuns.AnyAsync(e => e.Id == execution && e.RobotId == robot.Id && e.OperationId == item.OperationId, ct))
                throw ApiException.Conflict("event_conflict", "Event execution does not match the operation.");
            if (item.Sequence <= sequence)
            {
                var previous = await db.LogbookEvents.AsNoTracking().SingleOrDefaultAsync(e => e.RobotId == robot.Id && e.LocalSequence == item.Sequence, ct);
                if (previous is null || previous.Id != item.Id || previous.OperationId != item.OperationId || previous.OccurredAt != item.OccurredAt || previous.State != item.State ||
                    previous.Type != item.Type || previous.ExecutionId != item.ExecutionId || !JsonNode.DeepEquals(previous.Payload is null ? null : JsonNode.Parse(previous.Payload), item.Payload is null ? null : JsonNode.Parse(item.Payload)))
                    throw ApiException.Conflict("event_conflict", "An event ID or sequence was reused with different content.");
                continue;
            }
            if (item.Sequence != sequence + 1) throw ApiException.Conflict("sequence_gap", "The batch does not start at the next expected sequence.");
            if (await db.LogbookEvents.AnyAsync(e => e.Id == item.Id, ct))
                throw ApiException.Conflict("event_conflict", "An event ID or sequence was reused with different content.");
            db.LogbookEvents.Add(new LogbookEvent { Id = item.Id, RobotId = robot.Id, OperationId = item.OperationId, LocalSequence = item.Sequence,
                OccurredAt = item.OccurredAt, ReceivedAt = DateTimeOffset.UtcNow, Type = item.Type, SchemaVersion = 1, State = item.State, ExecutionId = item.ExecutionId, Payload = item.Payload });
            sequence = item.Sequence;
        }
        if (checkpoint is null) db.LogbookCheckpoints.Add(new LogbookCheckpoint { RobotId = robot.Id, Sequence = sequence });
        else checkpoint.Sequence = sequence;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        { throw ApiException.Conflict("event_conflict", "An event ID or sequence was reused with different content."); }
        await tx.CommitAsync(ct);
        return Results.Ok(new { acknowledged_sequence = sequence });
    }
}
