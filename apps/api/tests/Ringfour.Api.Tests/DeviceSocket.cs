using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ringfour.Api.Tests;

// WebSocket client-side helpers for exercising the real /v1/device/connect
// endpoint over TestServer. Every message is built per the frozen envelope in
// packages/contracts/schemas/device/v1.
internal static class DeviceSocket
{
    public static async Task<WebSocket> ConnectAsync(
        ApiFactory factory,
        Guid robotId,
        Guid credentialId,
        string secret,
        int protocolVersion = 1,
        string daemonVersion = "0.1.0")
    {
        var client = factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            request.Headers["x-ringfour-credential-id"] = credentialId.ToString();
            request.Headers["x-ringfour-credential-secret"] = secret;
        };
        var socket = await client.ConnectAsync(
            new Uri("ws://localhost/v1/device/connect"), CancellationToken.None);
        await SendAsync(socket, robotId, 1, "hello", new JsonObject
        {
            ["daemon_version"] = daemonVersion,
            ["supported_protocol_versions"] = new JsonArray(protocolVersion),
        });
        return socket;
    }

    public static async Task SendAsync(
        WebSocket socket, Guid robotId, int sequence, string type, JsonObject payload)
    {
        var envelope = new JsonObject
        {
            ["version"] = 1,
            ["message_id"] = Guid.NewGuid().ToString(),
            ["type"] = type,
            ["robot_id"] = robotId.ToString(),
            ["sequence"] = sequence,
            ["payload"] = payload,
        };
        await socket.SendAsync(
            Encoding.UTF8.GetBytes(envelope.ToJsonString()),
            WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
    }

    public static async Task<JsonDocument> ReceiveAsync(WebSocket socket)
    {
        var text = await ReceiveTextAsync(socket);
        return JsonDocument.Parse(text);
    }

    public static async Task<string> ReceiveTextAsync(WebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        var builder = new StringBuilder();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException(
                    $"socket closed unexpectedly with status {result.CloseStatus}");
            }
            builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (result.EndOfMessage)
            {
                return builder.ToString();
            }
        }
    }

    // Reads the next frame, which must be a close. Returns its status.
    public static async Task<WebSocketCloseStatus> ExpectCloseAsync(WebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return result.CloseStatus
                    ?? throw new InvalidOperationException("close frame had no status");
            }
        }
    }

    public static JsonElement Payload(this JsonDocument envelope) =>
        envelope.RootElement.GetProperty("payload");
}
