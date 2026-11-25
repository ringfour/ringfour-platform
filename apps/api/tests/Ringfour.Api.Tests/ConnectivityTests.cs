using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Ringfour.Api.Connectivity;
using Ringfour.Api.Persistence;

namespace Ringfour.Api.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ConnectivityTests(PostgresFixture postgres)
{
    private static string UniqueSub() => $"auth0|{Guid.NewGuid():N}";

    private static async Task<(Guid RobotId, Guid CredentialId, string Secret, HttpClient Owner)> EnrolledRobotAsync(
        ApiFactory factory)
    {
        var owner = factory.CreateAuthenticatedClient(UniqueSub());
        var orgId = await OrganizationTests.CreateOrganization(owner, "Acme Robotics");
        var robotId = await RobotTests.CreateRobot(owner, orgId, "warehouse-01");
        var (_, token) = await RobotTests.IssueToken(owner, robotId);
        var (credentialId, secret) = await DeviceEnrollmentTests.Enroll(factory, token);
        return (robotId, credentialId, secret, owner);
    }

    private static async Task<Guid> AddMemberAsync(
        HttpClient owner, Guid orgId, ApiFactory factory, string role)
    {
        var member = factory.CreateAuthenticatedClient(UniqueSub());
        var response = await owner.PostAsJsonAsync(
            $"/v1/organizations/{orgId}/members",
            new { user_id = await RobotTests.UserId(member), role },
            RingfourJson.Options);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await RobotTests.UserId(member);
    }

    [Fact]
    public async Task Concurrent_sends_keep_wire_sequence_ordered()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        var (robotId, credentialId, secret, _) = await EnrolledRobotAsync(factory);
        using var socket = await DeviceSocket.ConnectAsync(factory, robotId, credentialId, secret);
        await DeviceSocket.ReceiveAsync(socket);
        var manager = factory.Services.GetRequiredService<DeviceConnectionManager>();
        var session = Assert.IsType<DeviceConnection>(manager.Get(robotId));
        var sends = Enumerable.Range(0, 40).Select(_ => Task.Run(() =>
            manager.SendAsync(session, RingfourJson.Options, "heartbeat", new JsonObject()))).ToArray();
        for (var sequence = 2; sequence < 42; sequence++)
        {
            using var received = await DeviceSocket.ReceiveAsync(socket);
            Assert.Equal(sequence, received.RootElement.GetProperty("sequence").GetInt32());
        }
        await Task.WhenAll(sends);
    }

    [Fact]
    public async Task Fragmented_message_over_limit_is_rejected()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        var (robotId, credentialId, secret, _) = await EnrolledRobotAsync(factory);
        using var socket = await DeviceSocket.ConnectAsync(factory, robotId, credentialId, secret);
        await DeviceSocket.ReceiveAsync(socket);
        var fragment = Encoding.UTF8.GetBytes(new string('x', 32769));
        await socket.SendAsync(fragment, WebSocketMessageType.Text, false, CancellationToken.None);
        await socket.SendAsync(fragment, WebSocketMessageType.Text, true, CancellationToken.None);
        using var error = await DeviceSocket.ReceiveAsync(socket);
        Assert.Equal("malformed_message", error.Payload().GetProperty("code").GetString());
        Assert.Equal(4409, (int)await DeviceSocket.ExpectCloseAsync(socket));
    }

    [Theory]
    [InlineData("heartbeat", "{\"extra\":true}")]
    [InlineData("ack", "{\"operation_id\":5}")]
    [InlineData("operation_status", "{\"operation_id\":\"00000000-0000-0000-0000-000000000000\",\"state\":\"Succeeded\"}")]
    [InlineData("operation_status", "{\"operation_id\":\"00000000-0000-0000-0000-000000000000\",\"state\":\"running\",\"extra\":true}")]
    public async Task Invalid_message_payloads_are_rejected(string kind, string rawPayload)
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        var (robotId, credentialId, secret, _) = await EnrolledRobotAsync(factory);
        using var socket = await DeviceSocket.ConnectAsync(factory, robotId, credentialId, secret);
        await DeviceSocket.ReceiveAsync(socket);
        await DeviceSocket.SendAsync(socket, robotId, 2, kind, JsonNode.Parse(rawPayload)!.AsObject());
        using var error = await DeviceSocket.ReceiveAsync(socket);
        Assert.Equal("malformed_message", error.Payload().GetProperty("code").GetString());
        Assert.Equal(4409, (int)await DeviceSocket.ExpectCloseAsync(socket));
    }

    [Fact]
    public async Task Hello_returns_welcome_and_presence_is_tracked()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        var (robotId, credentialId, secret, owner) = await EnrolledRobotAsync(factory);

        using var socket = await DeviceSocket.ConnectAsync(factory, robotId, credentialId, secret);
        using var welcome = await DeviceSocket.ReceiveAsync(socket);

        Assert.Equal("welcome", welcome.RootElement.GetProperty("type").GetString());
        ContractSchema.AssertValid("device/v1/welcome.schema.json", welcome.Payload().GetRawText());
        var payload = welcome.Payload();
        Assert.Equal(15, payload.GetProperty("heartbeat_interval_seconds").GetInt32());
        var sessionId = payload.GetProperty("session_id").GetGuid();
        Assert.NotEqual(Guid.Empty, sessionId);
        Assert.True(payload.GetProperty("server_time").GetDateTime() <= DateTime.UtcNow.AddMinutes(1));

        // Observed status reflects the accepted session.
        var status = await owner.GetStringAsync($"/v1/robots/{robotId}/status");
        ContractSchema.AssertValid("robots/v1/robot-status-response.schema.json", status);
        var statusJson = JsonDocument.Parse(status).RootElement;
        Assert.True(statusJson.GetProperty("online").GetBoolean());
        Assert.Equal("0.1.0", statusJson.GetProperty("daemon_version").GetString());
        Assert.Equal(1, statusJson.GetProperty("protocol_version").GetInt32());
        Assert.True(statusJson.GetProperty("last_seen_at").ValueKind == JsonValueKind.String);

        var presence = await postgres.QueryAsync(
            "SELECT session_id, daemon_version, protocol_version FROM robot_presence WHERE robot_id = @robotId",
            ("@robotId", robotId));
        var row = Assert.Single(presence);
        Assert.Equal(sessionId, (Guid)row[0]!);
    }

    [Fact]
    public async Task Status_is_derived_from_server_receive_time()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        var (robotId, credentialId, secret, owner) = await EnrolledRobotAsync(factory);

        // Never connected: everything null, offline.
        var initial = JsonDocument.Parse(await owner.GetStringAsync($"/v1/robots/{robotId}/status")).RootElement;
        Assert.False(initial.GetProperty("online").GetBoolean());
        Assert.Equal(JsonValueKind.Null, initial.GetProperty("last_seen_at").ValueKind);

        using var socket = await DeviceSocket.ConnectAsync(factory, robotId, credentialId, secret);
        await DeviceSocket.ReceiveAsync(socket); // welcome

        // Boundary check with a controlled clock: exactly at the threshold the
        // robot is offline; one instant inside it is online.
        var threshold = TimeSpan.FromSeconds(45);
        var now = DateTimeOffset.UtcNow;
        Assert.False(OperationEndpoints.ComputeOnline(now - threshold, now, 45));
        Assert.True(OperationEndpoints.ComputeOnline(
            now - threshold + TimeSpan.FromTicks(1), now, 45));

        // Stale presence (as after a crash without disconnect) reads offline.
        await postgres.ExecuteAsync(
            "UPDATE robot_presence SET last_seen_at = now() - interval '2 minutes'" +
            " WHERE robot_id = @robotId",
            ("@robotId", robotId));
        var stale = JsonDocument.Parse(await owner.GetStringAsync($"/v1/robots/{robotId}/status")).RootElement;
        Assert.False(stale.GetProperty("online").GetBoolean());
        Assert.True(stale.GetProperty("last_seen_at").ValueKind == JsonValueKind.String);
    }

    [Fact]
    public async Task Unsupported_protocol_version_is_rejected_explicitly()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        var (robotId, credentialId, secret, _) = await EnrolledRobotAsync(factory);

        var client = factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            request.Headers["x-ringfour-credential-id"] = credentialId.ToString();
            request.Headers["x-ringfour-credential-secret"] = secret;
        };
        using var socket = await client.ConnectAsync(
            new Uri("ws://localhost/v1/device/connect"), CancellationToken.None);
        await DeviceSocket.SendAsync(socket, robotId, 1, "hello", new JsonObject
        {
            ["daemon_version"] = "0.1.0",
            ["supported_protocol_versions"] = new JsonArray(2, 3),
        });

        using var error = await DeviceSocket.ReceiveAsync(socket);
        Assert.Equal("error", error.RootElement.GetProperty("type").GetString());
        Assert.Equal(
            "unsupported_protocol_version",
            error.Payload().GetProperty("code").GetString());
        Assert.Equal(4402, (int)await DeviceSocket.ExpectCloseAsync(socket));

        // No presence was recorded for the rejected session.
        var presence = await postgres.QueryAsync(
            "SELECT count(*) FROM robot_presence WHERE robot_id = @robotId",
            ("@robotId", robotId));
        Assert.Equal(0L, presence[0][0]);
    }

    private static async Task AssertHandshakeDeniedAsync(ApiFactory factory, Guid credentialId, string secret)
    {
        // A rejected upgrade must never yield a usable socket. TestServer
        // surfaces the failure promptly as InvalidOperationException; the
        // deadline guards the case where it does not.
        var client = factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            request.Headers["x-ringfour-credential-id"] = credentialId.ToString();
            request.Headers["x-ringfour-credential-secret"] = secret;
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        WebSocket? socket = null;
        try
        {
            socket = await client.ConnectAsync(
                new Uri("ws://localhost/v1/device/connect"), deadline.Token);
        }
        catch (InvalidOperationException)
        {
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
        }
        Assert.True(socket is null || socket.State != WebSocketState.Open);
        socket?.Dispose();
    }

    [Fact]
    public async Task Invalid_credential_is_rejected_before_upgrade()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        var (robotId, credentialId, _, _) = await EnrolledRobotAsync(factory);

        await AssertHandshakeDeniedAsync(factory, credentialId, "rfs_wrong");
    }

    [Theory]
    [InlineData(1, "stale_sequence")] // duplicate sequence after hello
    [InlineData(4, "sequence_gap")]   // skipped sequences after hello
    public async Task Sequence_must_increase_by_exactly_one(
        int heartbeatSequence, string expectedCode)
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        var (robotId, credentialId, secret, _) = await EnrolledRobotAsync(factory);
        using var socket = await DeviceSocket.ConnectAsync(factory, robotId, credentialId, secret);
        await DeviceSocket.ReceiveAsync(socket); // welcome (hello consumed sequence 1)

        await DeviceSocket.SendAsync(socket, robotId, heartbeatSequence, "heartbeat", new JsonObject());
        using var error = await DeviceSocket.ReceiveAsync(socket);
        Assert.Equal(expectedCode, error.Payload().GetProperty("code").GetString());
        Assert.Equal(4409, (int)await DeviceSocket.ExpectCloseAsync(socket));
    }

    [Fact]
    public async Task First_message_must_be_hello_and_robot_id_must_match()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        var (robotId, credentialId, secret, _) = await EnrolledRobotAsync(factory);

        // A heartbeat as first message is a protocol violation.
        var client = factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            request.Headers["x-ringfour-credential-id"] = credentialId.ToString();
            request.Headers["x-ringfour-credential-secret"] = secret;
        };
        using var socket = await client.ConnectAsync(
            new Uri("ws://localhost/v1/device/connect"), CancellationToken.None);
        await DeviceSocket.SendAsync(socket, robotId, 1, "heartbeat", new JsonObject());
        using var error = await DeviceSocket.ReceiveAsync(socket);
        Assert.Equal("malformed_message", error.Payload().GetProperty("code").GetString());
        Assert.Equal(4409, (int)await DeviceSocket.ExpectCloseAsync(socket));

        // hello claiming another robot is not trusted.
        using var socket2 = await DeviceSocket.ConnectAsync(factory, robotId, credentialId, secret);
        await DeviceSocket.ReceiveAsync(socket2); // welcome consumed hello#1... replaced session below
        var client2 = factory.Server.CreateWebSocketClient();
        client2.ConfigureRequest = request =>
        {
            request.Headers["x-ringfour-credential-id"] = credentialId.ToString();
            request.Headers["x-ringfour-credential-secret"] = secret;
        };
        using var socket3 = await client2.ConnectAsync(
            new Uri("ws://localhost/v1/device/connect"), CancellationToken.None);
        await DeviceSocket.SendAsync(socket3, Guid.NewGuid(), 1, "hello", new JsonObject
        {
            ["daemon_version"] = "0.1.0",
            ["supported_protocol_versions"] = new JsonArray(1),
        });
        using var mismatch = await DeviceSocket.ReceiveAsync(socket3);
        Assert.Equal("robot_mismatch", mismatch.Payload().GetProperty("code").GetString());
        Assert.Equal(4409, (int)await DeviceSocket.ExpectCloseAsync(socket3));
    }







    [Fact]
    public async Task Newer_session_replaces_the_previous_one()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        var (robotId, credentialId, secret, _) = await EnrolledRobotAsync(factory);

        using var first = await DeviceSocket.ConnectAsync(factory, robotId, credentialId, secret);
        using var firstWelcome = await DeviceSocket.ReceiveAsync(first);
        var firstSession = firstWelcome.Payload().GetProperty("session_id").GetGuid();

        using var second = await DeviceSocket.ConnectAsync(factory, robotId, credentialId, secret);
        using var secondWelcome = await DeviceSocket.ReceiveAsync(second);

        // The replaced session is told and closed; the new one stays open.
        using var replaced = await DeviceSocket.ReceiveAsync(first);
        Assert.Equal("session_replaced", replaced.Payload().GetProperty("code").GetString());
        Assert.Equal(4409, (int)await DeviceSocket.ExpectCloseAsync(first));

        var presence = await postgres.QueryAsync(
            "SELECT session_id FROM robot_presence WHERE robot_id = @robotId",
            ("@robotId", robotId));
        Assert.Equal(secondWelcome.Payload().GetProperty("session_id").GetGuid(), (Guid)Assert.Single(presence)[0]!);
        Assert.NotEqual(firstSession, secondWelcome.Payload().GetProperty("session_id").GetGuid());
    }

    [Fact]
    public async Task Heartbeats_refresh_last_seen_only_for_the_current_session()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        var (robotId, credentialId, secret, _) = await EnrolledRobotAsync(factory);
        using var socket = await DeviceSocket.ConnectAsync(factory, robotId, credentialId, secret);
        await DeviceSocket.ReceiveAsync(socket); // welcome

        var before = (DateTime)(await postgres.QueryAsync(
            "SELECT last_seen_at FROM robot_presence WHERE robot_id = @robotId",
            ("@robotId", robotId)))[0][0]!;
        await Task.Delay(TimeSpan.FromMilliseconds(1100));
        await DeviceSocket.SendAsync(socket, robotId, 2, "heartbeat", new JsonObject());
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        var after = (DateTime)(await postgres.QueryAsync(
            "SELECT last_seen_at FROM robot_presence WHERE robot_id = @robotId",
            ("@robotId", robotId)))[0][0]!;
        Assert.True(after > before);
    }

    [Fact]
    public async Task Revocation_closes_the_live_session_and_denies_reconnect()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        var (robotId, credentialId, secret, owner) = await EnrolledRobotAsync(factory);
        using var socket = await DeviceSocket.ConnectAsync(factory, robotId, credentialId, secret);
        await DeviceSocket.ReceiveAsync(socket); // welcome

        // Persist revocation (owner route from milestone 02).
        var revoke = await owner.PostAsync(
            $"/v1/robots/{robotId}/credentials/{credentialId}/revoke", content: null);
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

        Assert.Equal(4401, (int)await DeviceSocket.ExpectCloseAsync(socket));

        // Reconnect with the same credential is denied at the handshake.
        await AssertHandshakeDeniedAsync(factory, credentialId, secret);
    }








}
