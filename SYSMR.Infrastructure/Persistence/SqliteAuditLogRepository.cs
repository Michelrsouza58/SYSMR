using System.Globalization;
using SYSMR.Application.Abstractions;
using SYSMR.Domain.Entities;

namespace SYSMR.Infrastructure.Persistence;

public sealed class SqliteAuditLogRepository(SqliteDatabase database) : IAuditLogRepository
{
    public async Task RecordAsync(string actor, string action, string details, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AuditLog (Id, CreatedAt, Actor, Action, Details)
            VALUES ($id, $createdAt, $actor, $action, $details);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$actor", actor.Trim());
        command.Parameters.AddWithValue("$action", action.Trim());
        command.Parameters.AddWithValue("$details", details.Trim());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AuditEntry>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, CreatedAt, Actor, Action, Details
            FROM AuditLog
            WHERE $query = ''
               OR Actor LIKE $pattern ESCAPE '\'
               OR Action LIKE $pattern ESCAPE '\'
               OR Details LIKE $pattern ESCAPE '\'
            ORDER BY CreatedAt DESC LIMIT 1000;
            """;
        command.Parameters.AddWithValue("$query", query.Trim());
        command.Parameters.AddWithValue("$pattern", $"%{EscapeLike(query.Trim())}%");
        var entries = new List<AuditEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new AuditEntry(
                Guid.Parse(reader.GetString(0)),
                DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4)));
        }
        return entries;
    }

    private static string EscapeLike(string value) =>
        value.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);
}
