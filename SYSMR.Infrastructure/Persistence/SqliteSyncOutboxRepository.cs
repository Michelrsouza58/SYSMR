using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SYSMR.Infrastructure.Persistence;

public sealed record PendingSyncEvent(
    string Id,
    string Branch,
    string Entity,
    string EntityId,
    string Payload);

public sealed record SyncQueueStatus(int PendingCount, string? LastError);

public sealed class SqliteSyncOutboxRepository(SqliteDatabase database)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task EnqueueAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string branch,
        string entity,
        string entityId,
        object payload,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid().ToString();
        var createdAt = DateTimeOffset.UtcNow;
        var body = JsonSerializer.Serialize(new
        {
            id,
            entity,
            entityId,
            createdAt = createdAt.ToString("O", CultureInfo.InvariantCulture),
            payload
        }, JsonOptions);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO SyncOutbox (Id, Branch, Entity, EntityId, Payload, CreatedAt)
            VALUES ($id, $branch, $entity, $entityId, $payload, $createdAt);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$branch", branch);
        command.Parameters.AddWithValue("$entity", entity);
        command.Parameters.AddWithValue("$entityId", entityId);
        command.Parameters.AddWithValue("$payload", body);
        command.Parameters.AddWithValue("$createdAt", createdAt.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PendingSyncEvent>> GetPendingAsync(
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(limit));
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Branch, Entity, EntityId, Payload
            FROM SyncOutbox
            WHERE SyncedAt IS NULL
            ORDER BY CreatedAt, Id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        var events = new List<PendingSyncEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            events.Add(new PendingSyncEvent(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        return events;
    }

    public async Task<SyncQueueStatus> GetStatusAsync(
        string branch,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*),
                   (SELECT LastError FROM SyncOutbox
                    WHERE Branch = $branch AND SyncedAt IS NULL AND LastError IS NOT NULL
                    ORDER BY CreatedAt LIMIT 1)
            FROM SyncOutbox
            WHERE Branch = $branch AND SyncedAt IS NULL;
            """;
        command.Parameters.AddWithValue("$branch", branch);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Não foi possível ler o estado da fila de sincronização.");
        return new SyncQueueStatus(
            reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    public async Task MarkSyncedAsync(PendingSyncEvent syncEvent, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                UPDATE SyncOutbox SET SyncedAt = $syncedAt, LastError = NULL
                WHERE Id = $id AND SyncedAt IS NULL;
                """;
            command.Parameters.AddWithValue("$syncedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$id", syncEvent.Id);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException($"O evento de sincronização {syncEvent.Id} não está pendente.");
        }
        if (syncEvent.Entity == "sale")
        {
            await using var source = connection.CreateCommand();
            source.Transaction = (SqliteTransaction)transaction;
            source.CommandText = "UPDATE Sales SET SyncStatus = 1 WHERE Id = $id;";
            source.Parameters.AddWithValue("$id", syncEvent.EntityId);
            await source.ExecuteNonQueryAsync(cancellationToken);
        }
        else if (syncEvent.Entity == "return")
        {
            await using var source = connection.CreateCommand();
            source.Transaction = (SqliteTransaction)transaction;
            source.CommandText = """
                UPDATE Returns SET SyncStatus = 1
                WHERE Id = $id AND NOT EXISTS (
                    SELECT 1 FROM SyncOutbox
                    WHERE Entity = 'return' AND EntityId = $id AND SyncedAt IS NULL
                );
                """;
            source.Parameters.AddWithValue("$id", syncEvent.EntityId);
            await source.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task MarkFailedAsync(
        string eventId,
        string error,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE SyncOutbox
            SET Attempts = Attempts + 1, LastError = $error
            WHERE Id = $id AND SyncedAt IS NULL;
            """;
        command.Parameters.AddWithValue("$error", error.Length <= 2000 ? error : error[..2000]);
        command.Parameters.AddWithValue("$id", eventId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException($"O evento de sincronização {eventId} não está pendente.");
    }

    public async Task UpdateRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        var protectedToken = WindowsSecretProtection.Protect(refreshToken);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE StoreAccountCache SET ProtectedRefreshToken = $token WHERE Id = 1;";
        command.Parameters.AddWithValue("$token", protectedToken);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Não existe conta local para armazenar a sessão Firebase renovada.");
    }
}
