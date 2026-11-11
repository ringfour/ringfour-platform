using System.Diagnostics.Metrics;
using Ringfour.Api.Connectivity;

namespace Ringfour.Api.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ConnectivityMetricsTests
{
    [Fact]
    public void Reports_queue_sessions_attempts_and_terminal_outcomes()
    {
        var values = new Dictionary<string, double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name == "Ringfour.Connectivity") current.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => values[instrument.Name] = value);
        listener.SetMeasurementEventCallback<double>((instrument, value, _, _) => values[instrument.Name] = value);
        listener.Start();
        using var metrics = new ConnectivityMetrics(new DeviceConnectionManager());
        metrics.ConnectionAttempt();
        metrics.Terminal("failed");
        metrics.Queue(3, 12.5);
        listener.RecordObservableInstruments();
        Assert.Equal(1, values["ringfour.device.connection_attempts"]);
        Assert.Equal(1, values["ringfour.operation.terminal"]);
        Assert.Equal(0, values["ringfour.device.connected"]);
        Assert.Equal(3, values["ringfour.operation.queued"]);
        Assert.Equal(12.5, values["ringfour.operation.oldest_queued_seconds"]);
    }
}
