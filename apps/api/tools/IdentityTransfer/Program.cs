using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;

if (args.Length != 2 || args[0] is not ("export" or "restore"))
{
    Console.Error.WriteLine("Usage: IDENTITY_TRANSFER_CONNECTION_STRING=<connection-string> IdentityTransfer <export|restore> <identity-bundle.json>");
    return 2;
}

var jsonOptions = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = false };
var connectionString = Environment.GetEnvironmentVariable("IDENTITY_TRANSFER_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("IDENTITY_TRANSFER_CONNECTION_STRING must be set.");
    return 2;
}
var (mode, path) = (args[0], Path.GetFullPath(args[1]));
try
{
    if (mode == "export") await ExportAsync(connectionString, path);
    else await RestoreAsync(connectionString, path);
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine($"Identity transfer failed: {error.Message}");
    return 1;
}

async Task ExportAsync(string connectionString, string path)
{
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
    var users = await ReadRowsAsync(connection, transaction, "users",
        "id, auth0_sub, name, email, profile_picture_url, phone_number, created_at");
    var organizations = await ReadRowsAsync(connection, transaction, "organizations", "id, name, created_at");
    var memberships = await ReadRowsAsync(connection, transaction, "memberships", "organization_id, user_id, role, created_at");
    await transaction.CommitAsync();
    var bundle = new IdentityBundle(1, users, organizations, memberships);
    var bytes = JsonSerializer.SerializeToUtf8Bytes(bundle, jsonOptions);
    var tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
    try
    {
        await File.WriteAllBytesAsync(tempPath, bytes);
        File.Move(tempPath, path, overwrite: false);
    }
    finally
    {
        if (File.Exists(tempPath)) File.Delete(tempPath);
    }
    Console.WriteLine($"Exported users={users.Count} organizations={organizations.Count} memberships={memberships.Count}; sha256={Convert.ToHexStringLower(SHA256.HashData(bytes))}; file={path}");
}

async Task<List<Dictionary<string, JsonElement>>> ReadRowsAsync(NpgsqlConnection connection,
    NpgsqlTransaction transaction, string table, string columns)
{
    await using var command = new NpgsqlCommand($"SELECT {columns} FROM {table} ORDER BY 1", connection, transaction);
    await using var reader = await command.ExecuteReaderAsync();
    var result = new List<Dictionary<string, JsonElement>>();
    while (await reader.ReadAsync())
    {
        var row = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var value = reader.IsDBNull(i) ? null : reader.GetValue(i) switch
            {
                Guid id => id.ToString("D"),
                DateTime timestamp => DateTime.SpecifyKind(timestamp, DateTimeKind.Utc).ToString("O"),
                DateTimeOffset timestamp => timestamp.ToUniversalTime().ToString("O"),
                var scalar => scalar
            };
            row[reader.GetName(i)] = JsonSerializer.SerializeToElement(value, jsonOptions);
        }
        result.Add(row);
    }
    return result;
}

async Task RestoreAsync(string connectionString, string path)
{
    var bundle = JsonSerializer.Deserialize<IdentityBundle>(await File.ReadAllBytesAsync(path), jsonOptions)
        ?? throw new InvalidDataException("Identity bundle is empty.");
    if (bundle.SchemaVersion != 1) throw new InvalidDataException("Unsupported identity bundle schema.");
    ValidateBundle(bundle);
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
    await AssertEmptyAsync(connection, transaction);
    foreach (var user in bundle.Users)
        await InsertAsync(connection, transaction, "users", ["id", "auth0_sub", "name", "email", "profile_picture_url", "phone_number", "created_at"], user);
    foreach (var organization in bundle.Organizations)
        await InsertAsync(connection, transaction, "organizations", ["id", "name", "created_at"], organization);
    foreach (var membership in bundle.Memberships)
        await InsertAsync(connection, transaction, "memberships", ["organization_id", "user_id", "role", "created_at"], membership);
    await VerifyAsync(connection, transaction, "users", bundle.Users.Count);
    await VerifyAsync(connection, transaction, "organizations", bundle.Organizations.Count);
    await VerifyAsync(connection, transaction, "memberships", bundle.Memberships.Count);
    await transaction.CommitAsync();
    Console.WriteLine($"Restored users={bundle.Users.Count} organizations={bundle.Organizations.Count} memberships={bundle.Memberships.Count}; identities and IDs preserved.");
}


void ValidateBundle(IdentityBundle bundle)
{
    string Required(Dictionary<string, JsonElement> row, string key) =>
        row.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : throw new InvalidDataException($"Missing string field: {key}");
    var users = bundle.Users.Select(row => Guid.Parse(Required(row, "id"))).ToHashSet();
    var orgs = bundle.Organizations.Select(row => Guid.Parse(Required(row, "id"))).ToHashSet();
    if (users.Count != bundle.Users.Count || orgs.Count != bundle.Organizations.Count) throw new InvalidDataException("Duplicate identity IDs.");
    if (bundle.Users.Select(row => Required(row, "auth0_sub")).Distinct(StringComparer.Ordinal).Count() != bundle.Users.Count)
        throw new InvalidDataException("Duplicate Auth0 subjects.");
    var membershipKeys = new HashSet<string>(StringComparer.Ordinal);
    foreach (var row in bundle.Memberships)
    {
        var org = Guid.Parse(Required(row, "organization_id")); var user = Guid.Parse(Required(row, "user_id"));
        if (!orgs.Contains(org) || !users.Contains(user)) throw new InvalidDataException("Membership references missing identity.");
        if (!new[] { "Owner", "Developer", "Operator" }.Contains(Required(row, "role"), StringComparer.Ordinal))
            throw new InvalidDataException("Unknown membership role.");
        if (!membershipKeys.Add($"{org:D}/{user:D}")) throw new InvalidDataException("Duplicate membership.");
    }
    foreach (var org in orgs)
        if (!bundle.Memberships.Any(row => Guid.Parse(Required(row, "organization_id")) == org && Required(row, "role") == "Owner"))
            throw new InvalidDataException($"Organization {org:D} has no owner.");
}

async Task AssertEmptyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction)
{
    await using var command = new NpgsqlCommand("SELECT (SELECT count(*) FROM users), (SELECT count(*) FROM organizations), (SELECT count(*) FROM memberships)", connection, transaction);
    await using var reader = await command.ExecuteReaderAsync();
    await reader.ReadAsync();
    if (reader.GetInt64(0) != 0 || reader.GetInt64(1) != 0 || reader.GetInt64(2) != 0)
        throw new InvalidOperationException("Restore target already contains identity rows; refusing merge.");
}

async Task InsertAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string table,
    string[] columns, Dictionary<string, JsonElement> row)
{
    var parameters = columns.Select((column, i) => $"@p{i}").ToArray();
    var sql = $"INSERT INTO {table} ({string.Join(",", columns)}) VALUES ({string.Join(",", parameters)})";
    await using var command = new NpgsqlCommand(sql, connection, transaction);
    for (var i = 0; i < columns.Length; i++)
    {
        var value = row[columns[i]];
        object? scalar = value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetInt64(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException($"Unsupported value in {columns[i]}.")
        };
        if (columns[i] is "id" or "organization_id" or "user_id") scalar = Guid.Parse((string)scalar!);
        if (columns[i] == "created_at") scalar = DateTimeOffset.Parse((string)scalar!).ToUniversalTime();
        command.Parameters.AddWithValue($"p{i}", scalar ?? DBNull.Value);
    }
    await command.ExecuteNonQueryAsync();
}

async Task VerifyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string table, int expected)
{
    await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table}", connection, transaction);
    var actual = Convert.ToInt32(await command.ExecuteScalarAsync());
    if (actual != expected) throw new InvalidOperationException($"Restore verification failed for {table}: {actual} != {expected}.");
}

sealed record IdentityBundle(int SchemaVersion, List<Dictionary<string, JsonElement>> Users,
    List<Dictionary<string, JsonElement>> Organizations, List<Dictionary<string, JsonElement>> Memberships);
