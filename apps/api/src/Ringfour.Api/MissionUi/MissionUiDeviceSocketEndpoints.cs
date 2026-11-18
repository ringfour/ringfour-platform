using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Ringfour.Api.Errors;
using Ringfour.Api.Persistence;
using Ringfour.Api.Robots;

namespace Ringfour.Api.MissionUi;

public static class MissionUiDeviceSocketEndpoints
{
    private const int MaxFrameBytes = 64 * 1024;
    private const int CloseUnauthorized = 4401;
    private const int CloseProtocolViolation = 4409;

    public static void MapMissionUiDeviceSocketEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/v1/device/ui-connect", Connect);

    private static async Task Connect(
        HttpContext context,
        RingfourDbContext db,
        MissionUiDeviceConnectionManager manager,
        CancellationToken cancellationToken)
    {
        if (!context.WebSockets.IsWebSocketRequest)
            throw ApiException.InvalidInput("This endpoint requires a WebSocket upgrade.");

        if (!Guid.TryParse(context.Request.Headers["x-ringfour-credential-id"], out var credentialId))
            throw ApiException.InvalidToken("A valid robot credential is required.");
        var (robot, credential) = await DeviceAuth.VerifyCredential(db, credentialId,
            context.Request.Headers["x-ringfour-credential-secret"], cancellationToken);

        var socket = await context.WebSockets.AcceptWebSocketAsync();
        var session = new MissionUiDeviceConnection
        {
            RobotId = robot.Id,
            CredentialId = credential.Id,
            SessionId = Guid.NewGuid(),
            Socket = socket,
        };
        var registered = false;
        try
        {
            var first = await ReceiveFrameAsync(socket, cancellationToken);
            if (first is null || !TryEnvelope(first, out var type, out var sequence, out var payload) ||
                type != "hello" || sequence != 1 || !ValidHello(payload))
            {
                await CloseAsync(socket, (WebSocketCloseStatus)CloseProtocolViolation, "hello_required");
                return;
            }

            session.LastClientSequence = 1;
            foreach (var item in payload!["executions"]!.AsArray())
            {
                if (item is null || !Guid.TryParse(item["execution_id"]?.GetValue<string>(), out var executionId))
                    continue;
                var state = item["state"]!.GetValue<string>();
                if (await ExecutionBelongsToRobotAsync(db, executionId, robot.Id, cancellationToken))
                    session.ExecutionStates[executionId] = state;
            }

            var replaced = manager.Register(session);
            registered = true;
            if (replaced is not null)
                await CloseAsync(replaced.Socket, WebSocketCloseStatus.EndpointUnavailable, "ui_session_replaced");
            await SendAsync(session, "welcome", new JsonObject
            {
                ["connection_id"] = session.SessionId.ToString("D"),
                ["heartbeat_interval_seconds"] = 30,
            }, cancellationToken);

            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var frame = await ReceiveFrameAsync(socket, cancellationToken);
                if (frame is null) break;
                if (!TryEnvelope(frame, out type, out sequence, out payload) ||
                    sequence != session.LastClientSequence + 1)
                {
                    await CloseAsync(socket, (WebSocketCloseStatus)CloseProtocolViolation, "sequence_or_frame_invalid");
                    break;
                }
                session.LastClientSequence = sequence;
                if (type == "heartbeat") continue;
                if (type != "ui_status" || !ValidStatus(payload) ||
                    !Guid.TryParse(payload!["execution_id"]!.GetValue<string>(), out var statusExecutionId) ||
                    !await ExecutionBelongsToRobotAsync(db, statusExecutionId, robot.Id, cancellationToken))
                {
                    await CloseAsync(socket, (WebSocketCloseStatus)CloseProtocolViolation, "ui_frame_invalid");
                    break;
                }
                session.ExecutionStates[statusExecutionId] = payload["state"]!.GetValue<string>();
            }
        }
        catch (InvalidDataException)
        {
            await CloseAsync(socket, (WebSocketCloseStatus)CloseProtocolViolation, "frame_too_large_or_invalid");
        }
        catch (WebSocketException)
        {
            // Reconnect restores this in-memory transport; execution state is
            // queried again from the Runner by the daemon.
        }
        finally
        {
            if (registered) manager.Unregister(robot.Id, session.SessionId);
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await CloseAsync(socket, WebSocketCloseStatus.NormalClosure, "closing");
            socket.Dispose();
        }
    }

    private static async Task<bool> ExecutionBelongsToRobotAsync(
        RingfourDbContext db, Guid executionId, Guid robotId, CancellationToken ct) =>
        await db.ScriptExecutions.AnyAsync(item => item.Id == executionId && item.RobotId == robotId, ct);

    private static bool ValidHello(JsonObject? payload)
    {
        try
        {
            return payload is not null && payload.Count == 2 &&
                Guid.TryParse(payload["daemon_instance_id"]?.GetValue<string>(), out _) &&
                payload["executions"] is JsonArray executions && executions.Count <= 64 &&
                executions.All(item => item is JsonObject entry && entry.Count == 2 &&
                    Guid.TryParse(entry["execution_id"]?.GetValue<string>(), out _) &&
                    IsOneOf(entry["state"]?.GetValue<string>(), "starting", "ready", "unavailable", "ended"));
        }
        catch (InvalidOperationException) { return false; }
    }

    private static bool ValidStatus(JsonObject? payload)
    {
        try
        {
            return payload is not null && payload.Count == 2 &&
                Guid.TryParse(payload["execution_id"]?.GetValue<string>(), out _) &&
                IsOneOf(payload["state"]?.GetValue<string>(), "starting", "ready", "unavailable", "ended");
        }
        catch (InvalidOperationException) { return false; }
    }

    private static bool IsOneOf(string? value, params string[] allowed) =>
        value is not null && allowed.Contains(value, StringComparer.Ordinal);

    private static bool TryEnvelope(string raw, out string? type, out long sequence, out JsonObject? payload)
    {
        type = null;
        sequence = 0;
        payload = null;
        try
        {
            var envelope = JsonNode.Parse(raw) as JsonObject;
            if (envelope is null || envelope.Count != 5 ||
                envelope["protocol_version"]?.GetValue<int>() != 1 ||
                !Guid.TryParse(envelope["message_id"]?.GetValue<string>(), out _) ||
                !long.TryParse(envelope["sequence"]?.ToJsonString(), out sequence)) return false;
            type = envelope["type"]?.GetValue<string>();
            payload = envelope["payload"] as JsonObject;
            return payload is not null && type is "hello" or "heartbeat" or "ui_status";
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    private static async Task<string?> ReceiveFrameAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var output = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text || output.Length + result.Count > MaxFrameBytes)
                throw new InvalidDataException();
            output.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }
        try { return new UTF8Encoding(false, true).GetString(output.ToArray()); }
        catch (DecoderFallbackException exception) { throw new InvalidDataException("Invalid UTF-8 frame.", exception); }
    }

    private static async Task SendAsync(MissionUiDeviceConnection session, string type, JsonObject payload, CancellationToken ct)
    {
        var envelope = new JsonObject
        {
            ["protocol_version"] = 1,
            ["message_id"] = Guid.NewGuid().ToString("D"),
            ["sequence"] = ++session.ServerSequence,
            ["type"] = type,
            ["payload"] = payload,
        };
        var bytes = Encoding.UTF8.GetBytes(envelope.ToJsonString());
        await session.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private static async Task CloseAsync(WebSocket socket, WebSocketCloseStatus status, string reason)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
        try { await socket.CloseOutputAsync(status, reason, CancellationToken.None); }
        catch (WebSocketException) { }
    }
}
