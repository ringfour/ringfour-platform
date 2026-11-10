using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Ringfour.Api.Errors;
using Ringfour.Api.Persistence;
using Ringfour.Api.Robots;

namespace Ringfour.Api.Connectivity;

// The authenticated /v1/device/connect WebSocket. The daemon authenticates
// with its robot credential in headers before the upgrade; identity is always
// derived from the verified credential. Message shapes are frozen in
// packages/contracts/schemas/device/v1.
public static class DeviceSocketEndpoints
{
    public const int ProtocolVersion = 1;
    public const int CloseUnauthorized = 4401;
    public const int CloseUnsupportedProtocol = 4402;
    public const int CloseProtocolViolation = 4409;

    public static void MapDeviceSocketEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/v1/device/connect", Connect);
    }

    private static async Task Connect(
        HttpContext httpContext,
        RingfourDbContext db,
        DeviceConnectionManager manager,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (!httpContext.WebSockets.IsWebSocketRequest)
        {
            throw ApiException.InvalidInput("This endpoint requires a WebSocket upgrade.");
        }

        Guid credentialId;
        var credentialIdHeader = httpContext.Request.Headers["x-ringfour-credential-id"].ToString();
        if (!Guid.TryParse(credentialIdHeader, out credentialId))
        {
            throw ApiException.InvalidToken("A valid robot credential is required.");
        }
        var secret = httpContext.Request.Headers["x-ringfour-credential-secret"].ToString();
        var (robot, credential) = await DeviceAuth.VerifyCredential(
            db, credentialId, secret, cancellationToken);

        // From here on the connection speaks the device protocol; failures
        // are reported with error envelopes and close statuses, not HTTP.
        var socket = await httpContext.WebSockets.AcceptWebSocketAsync();
        var session = new DeviceConnection
        {
            RobotId = robot.Id,
            SessionId = Guid.NewGuid(),
            CredentialId = credential.Id,
            Socket = socket,
        };
        var options = RingfourJson.Options;
        var heartbeatSeconds = configuration.GetValue("Connectivity:HeartbeatIntervalSeconds", 15);
        var helloAccepted = false;

        try
        {
            var buffer = new byte[64 * 1024];
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var message = await ReceiveMessageAsync(socket, buffer, cancellationToken);
                if (message is null)
                {
                    break;
                }

                JsonObject envelope;
                try
                {
                    envelope = JsonNode.Parse(message) as JsonObject
                        ?? throw new JsonException("Envelope must be a JSON object.");
                }
                catch (JsonException)
                {
                    await manager.CloseWithErrorAsync(
                        session, options, "malformed_message",
                        "The message is not valid JSON.", CloseProtocolViolation);
                    return;
                }

                int envelopeVersion;
                string? messageId;
                string? robotIdText;
                long sequence;
                JsonObject? payload;
                string? type;
                try
                {
                    envelopeVersion = envelope["version"]?.GetValue<int>() ?? -1;
                    messageId = envelope["message_id"]?.GetValue<string>();
                    robotIdText = envelope["robot_id"]?.GetValue<string>();
                    type = envelope["type"]?.GetValue<string>();
                    sequence = envelope["sequence"]!.GetValue<long>();
                    payload = envelope["payload"] as JsonObject;
                }
                catch (Exception exception)
                    when (exception is InvalidOperationException or FormatException)
                {
                    await manager.CloseWithErrorAsync(
                        session, options, "malformed_message",
                        "Envelope fields have the wrong shape.", CloseProtocolViolation);
                    return;
                }

                if (envelopeVersion != ProtocolVersion || messageId is null || payload is null)
                {
                    await manager.CloseWithErrorAsync(
                        session, options, "malformed_message",
                        "The envelope is missing required fields or has an unsupported version.",
                        CloseProtocolViolation);
                    return;
                }
                if (!Guid.TryParse(robotIdText, out var envelopeRobotId) ||
                    envelopeRobotId != robot.Id)
                {
                    await manager.CloseWithErrorAsync(
                        session, options, "robot_mismatch",
                        "The envelope robot_id does not match the authenticated credential.",
                        CloseProtocolViolation);
                    return;
                }

                if (!helloAccepted)
                {
                    if (type != "hello" || sequence != 1)
                    {
                        await manager.CloseWithErrorAsync(
                            session, options, "malformed_message",
                            "The first message of a connection must be hello with sequence 1.",
                            CloseProtocolViolation);
                        return;
                    }
                }
                else if (sequence <= session.LastClientSequence)
                {
                    await manager.CloseWithErrorAsync(
                        session, options, "stale_sequence",
                        "sequence must increase by exactly one per message.",
                        CloseProtocolViolation);
                    return;
                }
                else if (sequence > session.LastClientSequence + 1)
                {
                    await manager.CloseWithErrorAsync(
                        session, options, "sequence_gap",
                        "sequence must increase by exactly one per message.",
                        CloseProtocolViolation);
                    return;
                }
                session.LastClientSequence = sequence;

                switch (type)
                {
                    case "hello":
                        if (helloAccepted)
                        {
                            await manager.CloseWithErrorAsync(
                                session, options, "malformed_message",
                                "hello may only be sent once per connection.",
                                CloseProtocolViolation);
                            return;
                        }
                        var supported = payload["supported_protocol_versions"] as JsonArray;
                        bool supportsCurrent;
                        string? daemonVersion;
                        try
                        {
                            supportsCurrent = supported is not null && supported.Any(
                                node => node is not null && node.GetValue<int>() == ProtocolVersion);
                            daemonVersion = payload["daemon_version"]?.GetValue<string>();
                        }
                        catch (Exception exception)
                            when (exception is InvalidOperationException or FormatException)
                        {
                            await manager.CloseWithErrorAsync(
                                session, options, "malformed_message",
                                "hello payload fields have the wrong shape.",
                                CloseProtocolViolation);
                            return;
                        }
                        if (!supportsCurrent)
                        {
                            await manager.CloseWithErrorAsync(
                                session, options, "unsupported_protocol_version",
                                $"The platform supports device protocol version {ProtocolVersion}.",
                                CloseUnsupportedProtocol);
                            return;
                        }
                        if (daemonVersion is null || daemonVersion.Length > 100)
                        {
                            await manager.CloseWithErrorAsync(
                                session, options, "malformed_message",
                                "daemon_version is required and must be at most 100 characters.",
                                CloseProtocolViolation);
                            return;
                        }

                        helloAccepted = true;
                        var replaced = manager.Register(session);
                        await ReplacePresenceAsync(db, robot.Id, session, daemonVersion, cancellationToken);
                        await manager.SendAsync(session, options, "welcome", new JsonObject
                        {
                            ["server_time"] = RingfourJson.UtcTimestamp(DateTimeOffset.UtcNow),
                            ["heartbeat_interval_seconds"] = heartbeatSeconds,
                            ["session_id"] = session.SessionId.ToString(),
                        });
                        if (replaced is not null)
                        {
                            await manager.CloseWithErrorAsync(
                                replaced, options, "session_replaced",
                                "This connection was replaced by a newer session for the same robot.",
                                CloseProtocolViolation);
                        }
                        await ReplayNonterminalOperationsAsync(db, manager, session, options, cancellationToken);
                        break;

                    case "heartbeat":
                        await TouchPresenceAsync(db, robot.Id, session, cancellationToken);
                        break;

                    case "ack":
                        await HandleAckAsync(db, manager, session, options, payload, cancellationToken);
                        break;

                    case "operation_status":
                        await HandleOperationStatusAsync(
                            db, manager, session, options, payload, cancellationToken);
                        break;

                    default:
                        await manager.CloseWithErrorAsync(
                            session, options, "malformed_message",
                            $"Messages of type {type} are not accepted from devices.",
                            CloseProtocolViolation);
                        return;
                }
            }
        }
        catch (WebSocketException)
        {
            // The transport dropped; the durable state in PostgreSQL is the
            // source of truth and replay on the next connection covers it.
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            manager.Unregister(robot.Id, session.SessionId);
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None);
                }
                catch (WebSocketException)
                {
                }
            }
            socket.Dispose();
        }
    }

    // An accepted connection replaces the stored session unconditionally;
    // only afterwards do fenced updates from the current session apply.
    private static async Task ReplacePresenceAsync(
        RingfourDbContext db,
        Guid robotId,
        DeviceConnection session,
        string daemonVersion,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO robot_presence (robot_id, session_id, last_seen_at, daemon_version, protocol_version)
            VALUES ({robotId}, {session.SessionId}, {now}, {daemonVersion}, {ProtocolVersion})
            ON CONFLICT (robot_id) DO UPDATE SET
                session_id = EXCLUDED.session_id,
                last_seen_at = EXCLUDED.last_seen_at,
                daemon_version = EXCLUDED.daemon_version,
                protocol_version = EXCLUDED.protocol_version
            """, cancellationToken: cancellationToken);
    }

    private static async Task TouchPresenceAsync(
        RingfourDbContext db,
        Guid robotId,
        DeviceConnection session,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlAsync($"""
            UPDATE robot_presence SET last_seen_at = {now}
            WHERE robot_id = {robotId} AND session_id = {session.SessionId}
            """, cancellationToken: cancellationToken);
    }

    private static async Task ReplayNonterminalOperationsAsync(
        RingfourDbContext db,
        DeviceConnectionManager manager,
        DeviceConnection session,
        JsonSerializerOptions options,
        CancellationToken cancellationToken)
    {
        var operations = await db.Operations
            .Where(operation =>
                operation.RobotId == session.RobotId &&
                (operation.State == OperationState.Queued ||
                 operation.State == OperationState.Accepted ||
                 operation.State == OperationState.Running))
            .OrderBy(operation => operation.CreatedAt)
            .ThenBy(operation => operation.Id)
            .ToListAsync(cancellationToken);
        foreach (var operation in operations)
        {
            await manager.SendAsync(session, options, "command", CommandPayload(operation));
        }
    }

    public static JsonObject CommandPayload(Operation operation) => new()
    {
        ["operation_id"] = operation.Id.ToString(),
        ["kind"] = operation.Kind,
        ["payload_version"] = operation.PayloadVersion,
        ["payload"] = JsonNode.Parse(operation.Payload),
        ["deadline_at"] = RingfourJson.UtcTimestamp(operation.DeadlineAt),
    };

    private static async Task HandleAckAsync(
        RingfourDbContext db,
        DeviceConnectionManager manager,
        DeviceConnection session,
        JsonSerializerOptions options,
        JsonObject payload,
        CancellationToken cancellationToken)
    {
        var operation = await FindOperationAsync(db, session, payload, cancellationToken);
        if (operation is null)
        {
            await SendOperationErrorAsync(
                manager, session, options, "unknown_operation", "No such operation for this robot.");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (operation.State == OperationState.Queued)
        {
            operation.State = OperationState.Accepted;
            operation.AcknowledgedAt ??= now;
            operation.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
        }
        else if (OperationStates.IsTerminal(operation.State))
        {
            await SendOperationErrorAsync(
                manager, session, options, "state_conflict",
                $"The operation is already in the terminal state {operation.State}.");
        }
    }

    private static async Task HandleOperationStatusAsync(
        RingfourDbContext db,
        DeviceConnectionManager manager,
        DeviceConnection session,
        JsonSerializerOptions options,
        JsonObject payload,
        CancellationToken cancellationToken)
    {
        var stateText = payload["state"]?.GetValue<string>();
        if (stateText is null ||
            !Enum.TryParse<OperationState>(stateText, ignoreCase: true, out var state) ||
            state == OperationState.Queued)
        {
            await manager.CloseWithErrorAsync(
                session, options, "malformed_message",
                "state must be one of accepted, running, succeeded, failed, cancelled.",
                CloseProtocolViolation);
            return;
        }

        var operation = await FindOperationAsync(db, session, payload, cancellationToken);
        if (operation is null)
        {
            await SendOperationErrorAsync(
                manager, session, options, "unknown_operation", "No such operation for this robot.");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (OperationStates.IsTerminal(operation.State))
        {
            if (operation.State == state)
            {
                return; // Idempotent repeat of the same terminal report.
            }
            await SendOperationErrorAsync(
                manager, session, options, "state_conflict",
                $"The operation is already in the terminal state {operation.State}.");
            return;
        }

        operation.State = state;
        operation.UpdatedAt = now;
        if (OperationStates.IsTerminal(state))
        {
            operation.Result = payload["result"]?.ToJsonString(options) ?? "{}";
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task<Operation?> FindOperationAsync(
        RingfourDbContext db,
        DeviceConnection session,
        JsonObject payload,
        CancellationToken cancellationToken)
    {
        var operationIdText = payload["operation_id"]?.GetValue<string>();
        if (operationIdText is null || !Guid.TryParse(operationIdText, out var operationId))
        {
            return null;
        }
        return await db.Operations.SingleOrDefaultAsync(
            candidate => candidate.Id == operationId && candidate.RobotId == session.RobotId,
            cancellationToken);
    }

    private static async Task SendOperationErrorAsync(
        DeviceConnectionManager manager,
        DeviceConnection session,
        JsonSerializerOptions options,
        string code,
        string message) =>
        await manager.SendAsync(session, options, "error", new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
        });

    private static async Task<string?> ReceiveMessageAsync(
        WebSocket socket, byte[] buffer, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (result.EndOfMessage)
            {
                return builder.ToString();
            }
        }
    }
}
