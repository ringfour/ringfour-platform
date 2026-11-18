using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Ringfour.Api.Errors;
using Ringfour.Api.Identity;
using Ringfour.Api.Persistence;
using Ringfour.Api.Robots;
using Ringfour.Api.Storage;

namespace Ringfour.Api.Interactions;

public static class InteractionEndpoints
{
    private static readonly Regex ValuePattern = new("^[a-zA-Z][a-zA-Z0-9_.-]{0,63}$", RegexOptions.CultureInvariant);
    private sealed record DeviceSync(Guid RequestId, Guid ExecutionId, string Prompt, JsonElement Choices,
        DateTimeOffset? Deadline, string? TimeoutOutcome, string State, string? Outcome,
        Guid? AppliedResponseId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
    private sealed record Choice(string Value, string Label);

    public static void MapInteractionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var human = endpoints.MapGroup("/v1").RequireAuthorization();
        human.MapGet("/organizations/{orgId:guid}/interactions", List);
        human.MapGet("/interactions/{requestId:guid}", Get);
        human.MapPost("/interactions/{requestId:guid}/responses", Respond);
        endpoints.MapPut("/v1/device/interactions/{requestId:guid}", Sync);
    }

    private static object Document(HumanInteraction item) => new
    {
        request_id = item.Id, robot_id = item.RobotId, execution_id = item.ExecutionId,
        prompt = item.Prompt, choices = JsonNode.Parse(item.Choices), deadline = item.Deadline,
        timeout_outcome = item.TimeoutOutcome, state = item.State, outcome = item.Outcome,
        response = item.Response is null ? null : new
        {
            response_id = item.Response.Id, chosen_value = item.Response.ChosenValue,
            actor_user_id = item.Response.ActorUserId, status = item.Response.Status,
            submitted_at = item.Response.SubmittedAt, confirmed_at = item.Response.ConfirmedAt
        },
        created_at = item.CreatedAt, updated_at = item.UpdatedAt
    };

    private static Choice[] ParseChoices(JsonElement choices)
    {
        if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() is < 2 or > 12)
            throw ApiException.InvalidInput("Invalid interaction choices.");
        var result = new List<Choice>();
        foreach (var item in choices.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(new[] { "label", "value" }) is false ||
                item.GetProperty("value").ValueKind != JsonValueKind.String || item.GetProperty("label").ValueKind != JsonValueKind.String ||
                item.GetProperty("value").GetString() is not { } value || !ValuePattern.IsMatch(value) ||
                item.GetProperty("label").GetString() is not { } label || string.IsNullOrWhiteSpace(label) || label.Contains('\0') || label.EnumerateRunes().Count() > 160)
                throw ApiException.InvalidInput("Invalid interaction choices.");
            result.Add(new Choice(value, label));
        }
        if (result.Select(item => item.Value).Distinct(StringComparer.Ordinal).Count() != result.Count)
            throw ApiException.InvalidInput("Interaction choice values must be unique.");
        return [.. result];
    }

    private static async Task<(Guid OperationId, Guid ActorUserId)> ExecutionAttribution(
        RingfourDbContext db, Guid executionId, CancellationToken ct)
    {
        var script = await db.ScriptExecutions.AsNoTracking().Where(item => item.Id == executionId)
            .Select(item => new { OperationId = item.Operation.Id, item.ActorUserId }).SingleOrDefaultAsync(ct);
        if (script is not null) return (script.OperationId, script.ActorUserId);
        var development = await db.DevSessionRuns.AsNoTracking().Where(item => item.Id == executionId)
            .Select(item => new { item.OperationId, item.ActorUserId }).SingleOrDefaultAsync(ct);
        if (development is not null) return (development.OperationId, development.ActorUserId);
        throw ApiException.Conflict("interaction_conflict", "The execution attribution is unavailable.");
    }

    private static Guid TerminalEventId(Guid requestId, string state)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"ringfour-interaction:{requestId:D}:{state}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static void AddInteractionEvent(RingfourDbContext db, Guid eventId, Guid robotId,
        Guid operationId, Guid executionId, Guid? actorUserId, string type, DateTimeOffset occurredAt,
        object payload)
    {
        db.LogbookEvents.Add(new LogbookEvent
        {
            Id = eventId, RobotId = robotId, OperationId = operationId, LocalSequence = null,
            Type = type, SchemaVersion = 1, OccurredAt = occurredAt.ToUniversalTime(),
            ReceivedAt = DateTimeOffset.UtcNow, ExecutionId = executionId, ActorUserId = actorUserId,
            Payload = JsonSerializer.Serialize(payload, RingfourJson.Options)
        });
    }

    private static async Task AddTerminalInteractionEvent(RingfourDbContext db, HumanInteraction item,
        string state, DateTimeOffset occurredAt, CancellationToken ct)
    {
        var attribution = await ExecutionAttribution(db, item.ExecutionId, ct);
        if (state == "answered" && item.Response is { } response)
        {
            var choices = JsonSerializer.Deserialize<Choice[]>(item.Choices, RingfourJson.Options) ?? [];
            var label = choices.Single(choice => choice.Value == response.ChosenValue).Label;
            AddInteractionEvent(db, response.Id, item.RobotId, attribution.OperationId, item.ExecutionId,
                response.ActorUserId, "human.interaction.answered", occurredAt,
                new { message = "Human interaction answered", interaction_id = item.Id,
                    prompt = item.Prompt, selected_label = label, outcome = response.ChosenValue,
                    response_id = response.Id });
            return;
        }

        if (state is "timed_out" or "cancelled")
            AddInteractionEvent(db, TerminalEventId(item.Id, state), item.RobotId,
                attribution.OperationId, item.ExecutionId, null, $"human.interaction.{state}", occurredAt,
                new { message = state == "timed_out" ? "Human interaction timed out" : "Human interaction cancelled",
                    interaction_id = item.Id, prompt = item.Prompt, outcome = item.Outcome });
    }

    private static async Task<(Robot Robot, RobotCredential Credential)> Device(HttpContext context,
        RingfourDbContext db, CancellationToken ct)
    {
        if (!Guid.TryParse(context.Request.Headers["x-ringfour-credential-id"].ToString(), out var credentialId))
            throw ApiException.InvalidToken("A valid robot credential is required.");
        return await DeviceAuth.VerifyCredential(db, credentialId,
            context.Request.Headers["x-ringfour-credential-secret"].ToString(), ct);
    }

    private static async Task<IResult> Sync(HttpContext context, RingfourDbContext db, Guid requestId, CancellationToken ct)
    {
        var (robot, credential) = await Device(context, db, ct);
        DeviceSync body;
        try
        {
            var bytes = await ScriptRevisionEndpoints.ReadLimited(context.Request.Body, 16384, ct);
            var document = JsonNode.Parse(bytes) as JsonObject;
            var fields = new[] { "request_id", "execution_id", "prompt", "choices", "deadline", "timeout_outcome",
                "state", "outcome", "applied_response_id", "created_at", "updated_at" };
            if (document is null || document.Count != fields.Length || fields.Any(field => !document.ContainsKey(field)))
                throw new JsonException();
            body = JsonSerializer.Deserialize<DeviceSync>(bytes, RingfourJson.Options)
                ?? throw new JsonException();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw ApiException.InvalidInput("Invalid interaction synchronization.");
        }
        var choices = ParseChoices(body.Choices);
        if (body.RequestId != requestId || string.IsNullOrWhiteSpace(body.Prompt) || body.Prompt.Contains('\0') ||
            body.Prompt.EnumerateRunes().Count() > 1000 ||
            (body.Deadline is null) != (body.TimeoutOutcome is null) ||
            (body.TimeoutOutcome is not null && !choices.Any(item => item.Value == body.TimeoutOutcome)) ||
            body.State is not ("pending" or "answered" or "timed_out" or "cancelled") ||
            (body.State == "pending" && body.Outcome is not null) ||
            (body.State is "answered" or "timed_out" && body.Outcome is null) ||
            (body.State == "cancelled" && body.Outcome is not null) || body.UpdatedAt < body.CreatedAt)
            throw ApiException.InvalidInput("Invalid interaction synchronization.");
        if (body.State == "answered" && !choices.Any(item => item.Value == body.Outcome) ||
            body.State == "timed_out" &&
            (body.Deadline is null || body.TimeoutOutcome is null || body.Outcome != body.TimeoutOutcome))
            throw ApiException.InvalidInput("Invalid interaction outcome.");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM robots WHERE id = {robot.Id} FOR UPDATE", ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM robot_credentials WHERE id = {credential.Id} FOR UPDATE", ct);
        await db.Entry(credential).ReloadAsync(ct);
        if (credential.RevokedAt is not null || credential.ExpiresAt <= DateTimeOffset.UtcNow)
            throw ApiException.InvalidToken("The robot credential is invalid, expired, or revoked.");
        if (!await db.ScriptExecutions.AnyAsync(item => item.Id == body.ExecutionId && item.RobotId == robot.Id, ct) &&
            !await db.DevSessionRuns.AnyAsync(item => item.Id == body.ExecutionId && item.RobotId == robot.Id, ct))
            throw ApiException.Conflict("interaction_conflict", "The execution does not belong to this robot.");

        var item = await db.HumanInteractions.Include(candidate => candidate.Response)
            .SingleOrDefaultAsync(candidate => candidate.Id == requestId, ct);
        var choicesJson = body.Choices.GetRawText();
        if (item is null)
        {
            if (body.State == "answered")
                throw ApiException.Conflict("interaction_conflict", "An answered interaction requires a hosted response.");
            item = new HumanInteraction { Id = requestId, RobotId = robot.Id, ExecutionId = body.ExecutionId,
                Prompt = body.Prompt, Choices = choicesJson,
                Deadline = body.Deadline?.ToUniversalTime(), TimeoutOutcome = body.TimeoutOutcome,
                State = body.State, Outcome = body.Outcome,
                CreatedAt = body.CreatedAt.ToUniversalTime(), UpdatedAt = body.UpdatedAt.ToUniversalTime() };
            db.HumanInteractions.Add(item);
            var attribution = await ExecutionAttribution(db, body.ExecutionId, ct);
            var choiceList = choices.Select(choice => new { value = choice.Value, label = choice.Label }).ToArray();
            AddInteractionEvent(db, item.Id, item.RobotId, attribution.OperationId, item.ExecutionId,
                attribution.ActorUserId, "human.interaction.requested", item.CreatedAt,
                new { message = "Human input requested", interaction_id = item.Id,
                    prompt = item.Prompt, choices = choiceList });
            if (body.State is "timed_out" or "cancelled")
                await AddTerminalInteractionEvent(db, item, body.State, item.UpdatedAt, ct);
        }
        else
        {
            if (item.RobotId != robot.Id || item.ExecutionId != body.ExecutionId || item.Prompt != body.Prompt ||
                item.Deadline != body.Deadline?.ToUniversalTime() || item.TimeoutOutcome != body.TimeoutOutcome ||
                !JsonNode.DeepEquals(JsonNode.Parse(item.Choices), JsonNode.Parse(choicesJson)) ||
                item.CreatedAt != body.CreatedAt.ToUniversalTime())
                throw ApiException.Conflict("interaction_conflict", "The interaction was replayed with different input.");
            if (item.State != "pending" && (item.State != body.State || item.Outcome != body.Outcome))
                throw ApiException.Conflict("interaction_conflict", "The interaction already has another terminal outcome.");
            if (item.State == "pending" && body.State != "pending")
            {
                if (body.State == "answered" && (item.Response is null || body.AppliedResponseId != item.Response.Id || body.Outcome != item.Response.ChosenValue))
                    throw ApiException.Conflict("interaction_conflict", "The answered outcome does not match the submitted response.");
                item.State = body.State;
                item.Outcome = body.Outcome;
                item.UpdatedAt = body.UpdatedAt.ToUniversalTime();
                if (item.Response is not null)
                {
                    item.Response.Status = body.State == "answered" ? "applied" : body.State == "timed_out" ? "rejected_timed_out" : "rejected_cancelled";
                    item.Response.ConfirmedAt = DateTimeOffset.UtcNow;
                }
                await AddTerminalInteractionEvent(db, item, body.State, body.UpdatedAt.ToUniversalTime(), ct);
            }
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new { request_id = item.Id, response = item.State == "pending" && item.Response?.Status == "submitted" ? new
        {
            response_id = item.Response.Id, chosen_value = item.Response.ChosenValue,
            actor_user_id = item.Response.ActorUserId, submitted_at = item.Response.SubmittedAt
        } : null });
    }

    private static async Task<IResult> List(HttpContext context, RingfourDbContext db, Guid orgId,
        [FromQuery(Name = "robot_id")] Guid? robotId, [FromQuery(Name = "execution_id")] Guid? executionId,
        string? state, string? cursor, int? limit, CancellationToken ct)
    {
        var user = await IdentityAccess.ResolveOrCreateUser(db, IdentityAccess.RequireSubject(context), ct);
        await IdentityAccess.RequireMembership(db, orgId, user.Id, OrgRole.Operator, ct);
        if (state is not null && state is not ("pending" or "answered" or "timed_out" or "cancelled") || limit is < 1 or > 100)
            throw ApiException.InvalidInput("Invalid interaction filter.");
        var (before, id) = Pagination.DecodeCursor(cursor);
        var query = db.HumanInteractions.AsNoTracking().Include(item => item.Response)
            .Where(item => item.Robot.OrganizationId == orgId);
        if (robotId is not null) query = query.Where(item => item.RobotId == robotId);
        if (executionId is not null) query = query.Where(item => item.ExecutionId == executionId);
        if (state is not null) query = query.Where(item => item.State == state);
        if (before is not null) query = query.Where(item => item.CreatedAt < before || item.CreatedAt == before && item.Id.CompareTo(id!.Value) < 0);
        var pageSize = limit ?? 100;
        var rows = await query.OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id).Take(pageSize + 1).ToListAsync(ct);
        var page = rows.Take(pageSize).ToList();
        return Results.Ok(new { items = page.Select(Document),
            next_cursor = Pagination.NextCursor(rows.Count > pageSize, page, item => item.CreatedAt, item => item.Id) });
    }

    private static async Task<IResult> Get(HttpContext context, RingfourDbContext db, Guid requestId, CancellationToken ct)
    {
        var item = await db.HumanInteractions.Include(candidate => candidate.Response).Include(candidate => candidate.Robot)
            .SingleOrDefaultAsync(candidate => candidate.Id == requestId, ct)
            ?? throw ApiException.NotFound("The requested resource does not exist in this organization.");
        var user = await IdentityAccess.ResolveOrCreateUser(db, IdentityAccess.RequireSubject(context), ct);
        await IdentityAccess.RequireMembership(db, item.Robot.OrganizationId, user.Id, OrgRole.Operator, ct);
        return Results.Ok(Document(item));
    }

    private static async Task<IResult> Respond(HttpContext context, RingfourDbContext db, Guid requestId, CancellationToken ct)
    {
        var key = context.Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 200)
            throw ApiException.InvalidInput("Idempotency-Key is required and must not exceed 200 characters.");
        JsonObject? body;
        try { body = JsonNode.Parse(await ScriptRevisionEndpoints.ReadLimited(context.Request.Body, 4096, ct)) as JsonObject; }
        catch (JsonException) { throw ApiException.InvalidInput("Invalid interaction response."); }
        if (body is null || body.Count != 1 || body["chosen_value"] is not JsonValue choiceNode ||
            !choiceNode.TryGetValue<string>(out var chosen) || !ValuePattern.IsMatch(chosen))
            throw ApiException.InvalidInput("Invalid interaction response.");
        var user = await IdentityAccess.ResolveOrCreateUser(db, IdentityAccess.RequireSubject(context), ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var item = await db.HumanInteractions.Include(candidate => candidate.Response).Include(candidate => candidate.Robot)
            .SingleOrDefaultAsync(candidate => candidate.Id == requestId, ct)
            ?? throw ApiException.NotFound("The requested resource does not exist in this organization.");
        await IdentityAccess.RequireMembership(db, item.Robot.OrganizationId, user.Id, OrgRole.Operator, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM human_interactions WHERE id = {requestId} FOR UPDATE", ct);
        await db.Entry(item).ReloadAsync(ct);
        item.Response = await db.HumanInteractionResponses.SingleOrDefaultAsync(candidate => candidate.InteractionId == requestId, ct);
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(chosen)));
        if (item.Response is not null)
        {
            if (item.Response.ActorUserId == user.Id && item.Response.IdempotencyKey == key && item.Response.RequestFingerprint == fingerprint)
            {
                await transaction.CommitAsync(ct);
                return Results.Json(new { request_id = item.Id, response_id = item.Response.Id, status = "submitted", submitted_at = item.Response.SubmittedAt }, statusCode: 202);
            }
            throw ApiException.Conflict("interaction_already_answered", "Another response already won this interaction.");
        }
        if (item.State != "pending") throw ApiException.Conflict("interaction_terminal", "The interaction is already terminal.");
        if (item.Deadline is not null && item.Deadline <= DateTimeOffset.UtcNow)
            throw new ApiException(StatusCodes.Status410Gone, "interaction_expired", "The interaction deadline has passed.");
        var choices = JsonSerializer.Deserialize<Choice[]>(item.Choices, RingfourJson.Options) ?? [];
        if (!choices.Any(choice => choice.Value == chosen)) throw ApiException.InvalidInput("chosen_value is not an interaction choice.");
        var response = new HumanInteractionResponse { Id = Guid.NewGuid(), InteractionId = item.Id,
            ActorUserId = user.Id, ChosenValue = chosen, Status = "submitted", IdempotencyKey = key,
            RequestFingerprint = fingerprint, SubmittedAt = DateTimeOffset.UtcNow };
        db.HumanInteractionResponses.Add(response);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Json(new { request_id = item.Id, response_id = response.Id, status = response.Status,
            submitted_at = response.SubmittedAt }, statusCode: 202);
    }
}
