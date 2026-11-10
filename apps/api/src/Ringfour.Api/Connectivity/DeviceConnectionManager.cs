using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ringfour.Api.Connectivity;

// A live device session. One receive loop per socket; sends are serialized
// through SendLock because dispatch can race the socket handler (operation
// creation, sweeper redelivery, replacement notices).
public sealed class DeviceConnection
{
    public required Guid RobotId { get; init; }
    public required Guid SessionId { get; init; }
    public required Guid CredentialId { get; init; }
    public required WebSocket Socket { get; init; }
    public SemaphoreSlim SendLock { get; } = new(1, 1);
    public int ServerSequence { get; set; }
    public long LastClientSequence { get; set; }
}

// In-memory registry of live sessions. Durability lives in PostgreSQL
// (robot_presence, operations); this only routes envelopes to open sockets.
public sealed class DeviceConnectionManager
{
    private readonly ConcurrentDictionary<Guid, DeviceConnection> _sessions = new();

    public DeviceConnection? Get(Guid robotId) =>
        _sessions.TryGetValue(robotId, out var session) ? session : null;

    public IEnumerable<DeviceConnection> All() => _sessions.Values;

    // Registers the new session and returns the replaced one, if any. The
    // caller is responsible for closing the replaced socket.
    public DeviceConnection? Register(DeviceConnection session)
    {
        DeviceConnection? replaced = null;
        _sessions.AddOrUpdate(
            session.RobotId,
            session,
            (_, existing) =>
            {
                replaced = existing;
                return session;
            });
        return replaced;
    }

    // Fenced removal: a replaced connection's teardown must not unregister
    // the session that replaced it.
    public void Unregister(Guid robotId, Guid sessionId)
    {
        if (_sessions.TryGetValue(robotId, out var current) &&
            current.SessionId == sessionId)
        {
            _sessions.TryRemove(robotId, out _);
        }
    }

    public DeviceConnection? FindByCredential(Guid credentialId) =>
        _sessions.Values.FirstOrDefault(session => session.CredentialId == credentialId);

    public async Task SendAsync(DeviceConnection session, JsonSerializerOptions options, string type, JsonObject payload)
    {
        var envelope = new JsonObject
        {
            ["version"] = 1,
            ["message_id"] = Guid.NewGuid().ToString(),
            ["type"] = type,
            ["robot_id"] = session.RobotId.ToString(),
            ["sequence"] = ++session.ServerSequence,
            ["payload"] = payload,
        };
        var bytes = Encoding.UTF8.GetBytes(envelope.ToJsonString(options));

        await session.SendLock.WaitAsync();
        try
        {
            if (session.Socket.State == WebSocketState.Open)
            {
                await session.Socket.SendAsync(
                    bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
            }
        }
        finally
        {
            session.SendLock.Release();
        }
    }

    // Sends an error envelope and closes the connection with the given status.
    public async Task CloseWithErrorAsync(
        DeviceConnection session,
        JsonSerializerOptions options,
        string code,
        string message,
        int closeStatus)
    {
        await SendAsync(session, options, "error", new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
        });
        try
        {
            // Send the close frame without waiting for the peer's handshake
            // response; a dead device must not stall the caller.
            await session.Socket.CloseOutputAsync(
                (WebSocketCloseStatus)closeStatus, code, CancellationToken.None);
        }
        catch (WebSocketException)
        {
            // The peer is already gone.
        }
    }
}
