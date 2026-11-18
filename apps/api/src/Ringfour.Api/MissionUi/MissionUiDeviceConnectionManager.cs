using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace Ringfour.Api.MissionUi;

public sealed class MissionUiDeviceConnection
{
    public required Guid RobotId { get; init; }
    public required Guid CredentialId { get; init; }
    public required Guid SessionId { get; init; }
    public required WebSocket Socket { get; init; }
    public long LastClientSequence { get; set; }
    public long ServerSequence { get; set; }
    public ConcurrentDictionary<Guid, string> ExecutionStates { get; } = new();
}

// This registry is intentionally separate from DeviceConnectionManager: UI
// traffic must not consume the command and operation socket.
public sealed class MissionUiDeviceConnectionManager
{
    private readonly ConcurrentDictionary<Guid, MissionUiDeviceConnection> _sessions = new();

    public MissionUiDeviceConnection? Get(Guid robotId) =>
        _sessions.TryGetValue(robotId, out var session) ? session : null;

    public MissionUiDeviceConnection? Register(MissionUiDeviceConnection session)
    {
        MissionUiDeviceConnection? replaced = null;
        _sessions.AddOrUpdate(session.RobotId, session, (_, current) =>
        {
            replaced = current;
            return session;
        });
        return replaced;
    }

    public void Unregister(Guid robotId, Guid sessionId)
    {
        if (_sessions.TryGetValue(robotId, out var current) && current.SessionId == sessionId)
            ((ICollection<KeyValuePair<Guid, MissionUiDeviceConnection>>)_sessions)
                .Remove(new KeyValuePair<Guid, MissionUiDeviceConnection>(robotId, current));
    }
}
