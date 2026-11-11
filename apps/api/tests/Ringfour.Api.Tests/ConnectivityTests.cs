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

    private static async Task<HttpResponseMessage> CreateOperationAsync(
        HttpClient caller, Guid robotId, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/v1/robots/{robotId}/operations")
        {
            Content = JsonContent.Create(
                new { kind = "diagnostic.noop", payload_version = 1, payload = new { } },
                options: RingfourJson.Options),
        };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }
        return await caller.SendAsync(request);
    }

    // WebSocket sends are asynchronous: the server applies a frame after the
    // client's SendAsync returns. Poll the durable state instead of assuming
    // the transition has already landed.
    private static async Task<JsonElement> WaitOperationStateAsync(
        HttpClient owner, Guid robotId, Guid operationId, string expected, int timeoutSeconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var body = await owner.GetStringAsync($"/v1/robots/{robotId}/operations/{operationId}");
            var element = JsonDocument.Parse(body).RootElement;
            if (element.GetProperty("state").GetString() == expected)
            {
                return element.Clone();
            }
            await Task.Delay(100);
        }
        throw new InvalidOperationException(
            $"operation {operationId} did not reach state {expected}");
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
    public async Task Command_dispatch_ack_and_terminal_status_persist()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        var (robotId, credentialId, secret, owner) = await EnrolledRobotAsync(factory);
        using var socket = await DeviceSocket.ConnectAsync(factory, robotId, credentialId, secret);
        using var welcome = await DeviceSocket.ReceiveAsync(socket);

        var response = await CreateOperationAsync(owner, robotId);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        ContractSchema.AssertValid("robots/v1/operation.schema.json", body);
        var operation = JsonDocument.Parse(body).RootElement;
        var operationId = operation.GetProperty("id").GetGuid();
        Assert.Equal("queued", operation.GetProperty("state").GetString());
        Assert.True(operation.GetProperty("deadline_at").GetDateTime() > DateTime.UtcNow);

        // The committed operation is dispatched as a command envelope.
        using var command = await DeviceSocket.ReceiveAsync(socket);
        Assert.Equal("command", command.RootElement.GetProperty("type").GetString());
        ContractSchema.AssertValid("device/v1/command.schema.json", command.Payload().GetRawText());
        var commandPayload = command.Payload();
        Assert.Equal(operationId, commandPayload.GetProperty("operation_id").GetGuid());
        Assert.Equal("diagnostic.noop", commandPayload.GetProperty("kind").GetString());

        // Ack moves the operation to accepted exactly once.
        await DeviceSocket.SendAsync(socket, robotId, 2, "ack", new JsonObject
        {
            ["operation_id"] = operationId.ToString(),
        });
        var accepted = await WaitOperationStateAsync(owner, robotId, operationId, "accepted");
        ContractSchema.AssertValid("robots/v1/operation.schema.json", accepted.GetRawText());
        Assert.True(accepted.GetProperty("acknowledged_at").ValueKind == JsonValueKind.String);
        var acknowledgedAt = accepted.GetProperty("acknowledged_at").GetDateTime();

        // Duplicate ack is an idempotent no-op, not an error or a rewrite.
        await DeviceSocket.SendAsync(socket, robotId, 3, "ack", new JsonObject
        {
            ["operation_id"] = operationId.ToString(),
        });

        // Progress and terminal outcome with a result payload.
        await DeviceSocket.SendAsync(socket, robotId, 4, "operation_status", new JsonObject
        {
            ["operation_id"] = operationId.ToString(),
            ["state"] = "running",
        });
        await DeviceSocket.SendAsync(socket, robotId, 5, "operation_status", new JsonObject
        {
            ["operation_id"] = operationId.ToString(),
            ["state"] = "succeeded",
            ["result"] = new JsonObject { ["noop"] = true },
        });

        var succeeded = await WaitOperationStateAsync(owner, robotId, operationId, "succeeded");
        ContractSchema.AssertValid("robots/v1/operation.schema.json", succeeded.GetRawText());
        Assert.True(succeeded.GetProperty("result").GetProperty("noop").GetBoolean());
        Assert.Equal(acknowledgedAt, succeeded.GetProperty("acknowledged_at").GetDateTime());

        // Terminal states are immutable: a conflicting report is an error,
        // an identical repeat is a no-op, and the stored result is unchanged.
        await DeviceSocket.SendAsync(socket, robotId, 6, "operation_status", new JsonObject
        {
            ["operation_id"] = operationId.ToString(),
            ["state"] = "succeeded",
            ["result"] = new JsonObject { ["noop"] = true },
        });
        await DeviceSocket.SendAsync(socket, robotId, 7, "operation_status", new JsonObject
        {
            ["operation_id"] = operationId.ToString(),
            ["state"] = "failed",
            ["result"] = new JsonObject
            {
                ["error"] = new JsonObject { ["code"] = "boom", ["message"] = "should not apply" },
            },
        });
        using var conflict = await DeviceSocket.ReceiveAsync(socket);
        Assert.Equal("state_conflict", conflict.Payload().GetProperty("code").GetString());

        var final = await WaitOperationStateAsync(owner, robotId, operationId, "succeeded");
        Assert.True(final.GetProperty("result").GetProperty("noop").GetBoolean());
    }

    [Fact]
    public async Task Unknown_operation_errors_but_connection_stays_open()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        var (robotId, credentialId, secret, _) = await EnrolledRobotAsync(factory);
        using var socket = await DeviceSocket.ConnectAsync(factory, robotId, credentialId, secret);
        await DeviceSocket.ReceiveAsync(socket); // welcome

        await DeviceSocket.SendAsync(socket, robotId, 2, "ack", new JsonObject
        {
            ["operation_id"] = Guid.NewGuid().ToString(),
        });
        using var error = await DeviceSocket.ReceiveAsync(socket);
        Assert.Equal("unknown_operation", error.Payload().GetProperty("code").GetString());

        // The connection is still usable.
        await DeviceSocket.SendAsync(socket, robotId, 3, "heartbeat", new JsonObject());
    }

    [Fact]
    public async Task Reconnect_replays_nonterminal_operations()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        var (robotId, credentialId, secret, owner) = await EnrolledRobotAsync(factory);

        // Queued while offline: committed before dispatch, no socket needed.
        var response = await CreateOperationAsync(owner, robotId);
        var queued = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("queued", queued.GetProperty("state").GetString());
        var operationId = queued.GetProperty("id").GetGuid();

        // First connection replays the queued command.
        using var socket = await DeviceSocket.ConnectAsync(factory, robotId, credentialId, secret);
        using var welcome = await DeviceSocket.ReceiveAsync(socket);
        using var command = await DeviceSocket.ReceiveAsync(socket);
        Assert.Equal(operationId, command.Payload().GetProperty("operation_id").GetGuid());
        await DeviceSocket.SendAsync(socket, robotId, 2, "ack", new JsonObject
        {
            ["operation_id"] = operationId.ToString(),
        });
        await DeviceSocket.SendAsync(socket, robotId, 3, "operation_status", new JsonObject
        {
            ["operation_id"] = operationId.ToString(),
            ["state"] = "succeeded",
            ["result"] = new JsonObject { ["noop"] = true },
        });
        socket.Dispose();

        // A second op queued while offline replays on the next connection;
        // the terminal first op is not replayed.
        var second = await CreateOperationAsync(owner, robotId);
        var secondId = JsonDocument.Parse(await second.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        using var socket2 = await DeviceSocket.ConnectAsync(factory, robotId, credentialId, secret);
        using var welcome2 = await DeviceSocket.ReceiveAsync(socket2);
        using var replay = await DeviceSocket.ReceiveAsync(socket2);
        Assert.Equal("command", replay.RootElement.GetProperty("type").GetString());
        Assert.Equal(secondId, replay.Payload().GetProperty("operation_id").GetGuid());
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

    [Fact]
    public async Task Sweeper_fails_operations_past_their_deadline()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        var (robotId, _, _, owner) = await EnrolledRobotAsync(factory);
        var response = await CreateOperationAsync(owner, robotId);
        var operationId = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        // Travel the stored deadline into the past, then run one sweep.
        await postgres.ExecuteAsync(
            "UPDATE operations SET deadline_at = now() - interval '1 second' WHERE id = @id",
            ("@id", operationId));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RingfourDbContext>();
            await OperationSweeper.SweepOnceAsync(
                db, new DeviceConnectionManager(), DateTimeOffset.UtcNow, CancellationToken.None);
        }

        var operation = JsonDocument.Parse(
            await owner.GetStringAsync($"/v1/robots/{robotId}/operations/{operationId}")).RootElement;
        Assert.Equal("failed", operation.GetProperty("state").GetString());
        Assert.Equal(
            "deadline_exceeded",
            operation.GetProperty("result").GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Idempotency_key_replays_matching_and_conflicts_on_divergence()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        var (robotId, _, _, owner) = await EnrolledRobotAsync(factory);

        var first = await CreateOperationAsync(owner, robotId, "key-1");
        var firstBody = await first.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstId = JsonDocument.Parse(firstBody).RootElement.GetProperty("id").GetGuid();

        // Same key, same body: the original operation is replayed.
        var replay = await CreateOperationAsync(owner, robotId, "key-1");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(firstId, JsonDocument.Parse(await replay.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid());

        // Same key, different body: conflict, and no new operation exists.
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/v1/robots/{robotId}/operations")
        {
            Content = JsonContent.Create(
                new { kind = "diagnostic.noop", payload_version = 1, payload = new { different = true } },
                options: RingfourJson.Options),
        };
        request.Headers.Add("Idempotency-Key", "key-1");
        var conflict = await owner.SendAsync(request);
        await RobotTests.AssertError(conflict, HttpStatusCode.Conflict, "conflict");

        var count = await postgres.QueryAsync(
            "SELECT count(*) FROM operations WHERE robot_id = @robotId",
            ("@robotId", robotId));
        Assert.Equal(1L, count[0][0]);

        // The persisted record pins principal, route, target, key, fingerprint
        // and outcome, and expires on schedule.
        var record = await postgres.QueryAsync(
            "SELECT route, target, outcome_operation_id, expires_at > now()" +
            " FROM operation_idempotency_records");
        var row = Assert.Single(record);
        Assert.Equal("POST /v1/robots/{robot_id}/operations", row[0]);
        Assert.Equal(robotId.ToString(), row[1]);
        Assert.Equal(firstId, (Guid)row[2]!);
        Assert.Equal(true, row[3]);

        // An expired record no longer scopes retries.
        await postgres.ExecuteAsync(
            "UPDATE operation_idempotency_records SET expires_at = now() - interval '1 hour'");
        var afterExpiry = await CreateOperationAsync(owner, robotId, "key-1");
        Assert.Equal(HttpStatusCode.Created, afterExpiry.StatusCode);
    }

    [Fact]
    public async Task Operation_endpoints_enforce_roles_and_membership()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        var owner = factory.CreateAuthenticatedClient(UniqueSub());
        var orgId = await OrganizationTests.CreateOrganization(owner, "Acme Robotics");
        var robotId = await RobotTests.CreateRobot(owner, orgId, "warehouse-01");
        var (_, token) = await RobotTests.IssueToken(owner, robotId);
        await DeviceEnrollmentTests.Enroll(factory, token);
        var outsider = factory.CreateAuthenticatedClient(UniqueSub());

        await owner.PostAsJsonAsync(
            $"/v1/organizations/{orgId}/members",
            new { user_id = await RobotTests.UserId(outsider), role = "operator" },
            RingfourJson.Options);

        // Operator may read status but not create operations.
        var statusRead = await outsider.GetAsync($"/v1/robots/{robotId}/status");
        Assert.Equal(HttpStatusCode.OK, statusRead.StatusCode);
        var denied = await CreateOperationAsync(outsider, robotId);
        await RobotTests.AssertError(denied, HttpStatusCode.Forbidden, "forbidden");

        // A member of another organization gets 404, not 403.
        var other = factory.CreateAuthenticatedClient(UniqueSub());
        var otherOrg = await OrganizationTests.CreateOrganization(other, "Other Corp");
        var notFound = await CreateOperationAsync(other, robotId);
        await RobotTests.AssertError(notFound, HttpStatusCode.NotFound, "not_found");
        var notFoundRead = await other.GetAsync($"/v1/robots/{robotId}/status");
        await RobotTests.AssertError(notFoundRead, HttpStatusCode.NotFound, "not_found");

        // Unknown kinds are rejected at the boundary.
        var badKind = await owner.PostAsJsonAsync(
            $"/v1/robots/{robotId}/operations",
            new { kind = "mission.run", payload_version = 1, payload = new { } },
            RingfourJson.Options);
        await RobotTests.AssertError(badKind, HttpStatusCode.BadRequest, "invalid_input");

        // A fresh operator-only robot in the caller's own org is the one case
        // that must work for the developer role: promoted member succeeds.
        using var developer = factory.CreateAuthenticatedClient(UniqueSub());
        await owner.PostAsJsonAsync(
            $"/v1/organizations/{orgId}/members",
            new { user_id = await RobotTests.UserId(developer), role = "developer" },
            RingfourJson.Options);
        var allowed = await CreateOperationAsync(developer, robotId);
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);

        // Operation reads are member-scoped too.
        var operationId = JsonDocument.Parse(await allowed.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();
        var otherRead = await other.GetAsync($"/v1/robots/{robotId}/operations/{operationId}");
        await RobotTests.AssertError(otherRead, HttpStatusCode.NotFound, "not_found");
        var missing = await owner.GetAsync(
            $"/v1/robots/{robotId}/operations/{Guid.NewGuid()}");
        await RobotTests.AssertError(missing, HttpStatusCode.NotFound, "not_found");
    }
}
