using Npgsql;

namespace Ringfour.Api.Tests;

[Collection(PostgresCollection.Name)]
public sealed class DevSessionPersistenceTests(PostgresFixture postgres)
{
    [Fact]
    public async Task SessionAndGenerationConstraintsSurviveConcurrentWrites()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        var client = factory.CreateAuthenticatedClient($"auth0|{Guid.NewGuid()}");
        var org = await OrganizationTests.CreateOrganization(client, "dev persistence");
        var robot = await RobotTests.CreateRobot(client, org, "dev robot");
        var owner = (Guid)(await postgres.QueryAsync("SELECT user_id FROM memberships WHERE organization_id = @org", ("org", org)))[0][0]!;
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        async Task<string> InsertSession(Guid id)
        {
            try
            {
                await postgres.ExecuteAsync("""
                    INSERT INTO dev_sessions (id, robot_id, owner_user_id, state, generation, created_at, updated_at, expires_at)
                    VALUES (@id, @robot, @owner, 'creating', 0, now(), now(), now() + interval '1 hour')
                    """, ("id", id), ("robot", robot), ("owner", owner));
                return "ok";
            }
            catch (PostgresException ex) { return ex.SqlState; }
        }
        var results = await Task.WhenAll(InsertSession(first), InsertSession(second));
        Assert.Single(results, result => result == "ok");
        Assert.Single(results, result => result == PostgresErrorCodes.UniqueViolation);
        var session = results[0] == "ok" ? first : second;
        var invalid = await Assert.ThrowsAsync<PostgresException>(() => postgres.ExecuteAsync(
            "UPDATE dev_sessions SET active_generation = 1 WHERE id = @id", ("id", session)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, invalid.SqlState);
        var operation = Guid.NewGuid();
        await postgres.ExecuteAsync("""
            INSERT INTO operations (id, robot_id, kind, payload_version, payload, state, created_at, updated_at, deadline_at)
            VALUES (@id, @robot, 'dev.session.sync', 1, '{}', 'Queued', now(), now(), now() + interval '1 hour')
            """, ("id", operation), ("robot", robot));
        var generation = Guid.NewGuid();
        await postgres.ExecuteAsync("""
            INSERT INTO dev_session_generations (id, session_id, operation_id, generation, state, archive_sha256, archive_size, created_at)
            VALUES (@id, @session, @operation, 1, 'pending', repeat('a', 64), 1, now())
            """, ("id", generation), ("session", session), ("operation", operation));
        var invalidSize = await Assert.ThrowsAsync<PostgresException>(() => postgres.ExecuteAsync(
            "UPDATE dev_session_generations SET archive_size = 1048577 WHERE id = @id", ("id", generation)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, invalidSize.SqlState);
        await postgres.ExecuteAsync("UPDATE dev_session_generations SET state = 'failed' WHERE id = @id", ("id", generation));
        var retry = Guid.NewGuid();
        await postgres.ExecuteAsync("""
            INSERT INTO operations (id, robot_id, kind, payload_version, payload, state, created_at, updated_at, deadline_at)
            VALUES (@id, @robot, 'dev.session.sync', 1, '{}', 'Queued', now(), now(), now() + interval '1 hour')
            """, ("id", retry), ("robot", robot));
        await postgres.ExecuteAsync("""
            INSERT INTO dev_session_generations (id, session_id, operation_id, generation, state, archive_sha256, archive_size, created_at)
            VALUES (@id, @session, @operation, 1, 'pending', repeat('b', 64), 1, now())
            """, ("id", Guid.NewGuid()), ("session", session), ("operation", retry));
        var anotherOperation = Guid.NewGuid();
        await postgres.ExecuteAsync("""
            INSERT INTO operations (id, robot_id, kind, payload_version, payload, state, created_at, updated_at, deadline_at)
            VALUES (@id, @robot, 'dev.session.sync', 1, '{}', 'Queued', now(), now(), now() + interval '1 hour')
            """, ("id", anotherOperation), ("robot", robot));
        var duplicatePending = await Assert.ThrowsAsync<PostgresException>(() => postgres.ExecuteAsync("""
            INSERT INTO dev_session_generations (id, session_id, operation_id, generation, state, archive_sha256, archive_size, created_at)
            VALUES (@id, @session, @operation, 2, 'pending', repeat('c', 64), 1, now())
            """, ("id", Guid.NewGuid()), ("session", session), ("operation", anotherOperation)));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicatePending.SqlState);
        await postgres.ExecuteAsync("UPDATE dev_sessions SET state = 'closed', closed_at = now() WHERE id = @id", ("id", session));
        Assert.Equal("ok", await InsertSession(Guid.NewGuid()));
    }
}
