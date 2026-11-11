using System.Diagnostics.Metrics;

namespace Ringfour.Api.Connectivity;

// Standard .NET Meter instruments; an OpenTelemetry or diagnostics collector
// must subscribe to the Ringfour.Connectivity meter to export them.
public sealed class ConnectivityMetrics : IDisposable
{
    private readonly Meter _meter = new("Ringfour.Connectivity", "1.0.0");
    private readonly Counter<long> _attempts;
    private readonly Counter<long> _terminal;
    private long _queued;
    private double _oldestSeconds;

    public ConnectivityMetrics(DeviceConnectionManager manager)
    {
        _attempts = _meter.CreateCounter<long>("ringfour.device.connection_attempts");
        _terminal = _meter.CreateCounter<long>("ringfour.operation.terminal");
        _meter.CreateObservableGauge("ringfour.device.connected", () => manager.All().LongCount());
        _meter.CreateObservableGauge("ringfour.operation.queued", () => Interlocked.Read(ref _queued));
        _meter.CreateObservableGauge("ringfour.operation.oldest_queued_seconds", () => Volatile.Read(ref _oldestSeconds), "s");
    }

    public void ConnectionAttempt() => _attempts.Add(1);
    public void Terminal(string state) => _terminal.Add(1, new KeyValuePair<string, object?>("state", state));
    public void Queue(long count, double oldestSeconds)
    {
        Interlocked.Exchange(ref _queued, count);
        Volatile.Write(ref _oldestSeconds, oldestSeconds);
    }
    public void Dispose() => _meter.Dispose();
}
