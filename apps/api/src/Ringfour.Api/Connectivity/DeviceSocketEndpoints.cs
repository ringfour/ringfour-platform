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
        ILoggerFactory loggerFactory,
        ConnectivityMetrics metrics,
        CancellationToken cancellationToken)
    {
        metrics.ConnectionAttempt();
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
        var logger = loggerFactory.CreateLogger("Ringfour.Connectivity");

        try
        {
            var buffer = new byte[64 * 1024];
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                string? message;
                try
                {
                    message = await ReceiveMessageAsync(socket, buffer, cancellationToken);
                }
                catch (InvalidDataException)
                {
                    await manager.CloseWithErrorAsync(session, options, "malformed_message",
                        "Messages must be valid UTF-8 text and at most 65536 bytes.", CloseProtocolViolation);
                    return;
                }
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
                    sequence = envelope["sequence"]?.GetValue<long>() ?? -1;
                    payload = envelope["payload"] as JsonObject;
                }
                catch (Exception exception)
                    when (exception is InvalidOperationException or FormatException or ArgumentException)
                {
                    await manager.CloseWithErrorAsync(
                        session, options, "malformed_message",
                        "Envelope fields have the wrong shape.", CloseProtocolViolation);
                    return;
                }

                if (envelopeVersion != ProtocolVersion || messageId is null || payload is null ||
                    !ValidMessage(envelope, type, messageId, sequence, payload))
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
                        if (daemonVersion is null || daemonVersion.EnumerateRunes().Count() > 100)
                        {
                            await manager.CloseWithErrorAsync(
                                session, options, "malformed_message",
                                "daemon_version is required and must be at most 100 characters.",
                                CloseProtocolViolation);
                            return;
                        }

                        helloAccepted = true;
                        var replaced = manager.Register(session);
                        logger.LogInformation("Device session {SessionId} connected for robot {RobotId}; replaced={Replaced}",
                            session.SessionId, session.RobotId, replaced is not null);
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
                        await HandleAckAsync(db, manager, session, options, payload, logger, cancellationToken);
                        break;

                    case "operation_status":
                        await HandleOperationStatusAsync(
                            db, manager, session, options, payload, logger, metrics, cancellationToken);
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
            if (helloAccepted)
                logger.LogInformation("Device session {SessionId} disconnected for robot {RobotId}", session.SessionId, robot.Id);
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
        ILogger logger,
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
            logger.LogInformation("Operation {OperationId} accepted for robot {RobotId} in session {SessionId}",
                operation.Id, session.RobotId, session.SessionId);
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
        ILogger logger,
        ConnectivityMetrics metrics,
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
        if (OperationStates.IsTerminal(state)) metrics.Terminal(state.ToString().ToLowerInvariant());
        logger.LogInformation("Operation {OperationId} reported {OperationState} for robot {RobotId} in session {SessionId}",
            operation.Id, state, session.RobotId, session.SessionId);
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
        using var bytes = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            if (result.MessageType != WebSocketMessageType.Text || bytes.Length + result.Count > 65536)
            {
                throw new InvalidDataException();
            }
            bytes.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                try { return new UTF8Encoding(false, true).GetString(bytes.ToArray()); }
                catch (DecoderFallbackException) { throw new InvalidDataException(); }
            }
        }
    }

    private static bool ValidMessage(JsonObject envelope, string? type, string messageId, long sequence, JsonObject payload)
    {
        try
        {
            if (envelope.Count != 6 || envelope.Any(pair => pair.Key is not
                    ("version" or "message_id" or "type" or "robot_id" or "sequence" or "payload")) ||
                messageId.EnumerateRunes().Count() is < 1 or > 100 || sequence < 1)
                return false;
            static bool ValidId(JsonNode? node) => node is not null &&
                Guid.TryParseExact(node.GetValue<string>(), "D", out var id) &&
                id.ToString() == node.GetValue<string>();
            if (!ValidId(envelope["robot_id"])) return false;
            return type switch
            {
                "hello" => payload.Count == 2 &&
                    payload["daemon_version"]?.GetValue<string>().EnumerateRunes().Count() is >= 1 and <= 100 &&
                    payload["supported_protocol_versions"] is JsonArray versions && versions.Count > 0 &&
                    versions.All(value => value is not null && value.GetValue<int>() >= 1),
                "heartbeat" => payload.Count == 0,
                "ack" => payload.Count == 1 && ValidId(payload["operation_id"]),
                "operation_status" => payload.Count is 2 or 3 &&
                    payload.All(pair => pair.Key is "operation_id" or "state" or "result") &&
                    ValidId(payload["operation_id"]) &&
                    payload["state"]?.GetValue<string>() is "accepted" or "running" or "succeeded" or "failed" or "cancelled" &&
                    (!payload.ContainsKey("result") || payload["result"] is JsonObject),
                _ => false,
            };
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException or ArgumentException)
        {
            return false;
        }
    }
}
