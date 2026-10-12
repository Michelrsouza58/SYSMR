using System.Globalization;
using SYSMR.Application.Abstractions;
using SYSMR.Domain.Entities;

namespace SYSMR.Infrastructure.Persistence;

public sealed class SqliteCustomerRepository(SqliteDatabase database) : ICustomerRepository
{
    public async Task<IReadOnlyList<Customer>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Name, Document, Phone, Email, RegisteredAt, ExpiresAt
            FROM Customers
            WHERE $query = '' OR Name LIKE $pattern ESCAPE '\' OR Document LIKE $pattern ESCAPE '\'
            ORDER BY Name LIMIT 500;
            """;
        command.Parameters.AddWithValue("$query", query.Trim());
        command.Parameters.AddWithValue("$pattern", $"%{EscapeLike(query.Trim())}%");
        var customers = new List<Customer>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            customers.Add(new Customer(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture)));
        }
        return customers;
    }

    public async Task SaveAsync(Customer customer, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Customers (Id, Name, Document, Phone, Email, RegisteredAt, ExpiresAt)
            VALUES ($id, $name, $document, $phone, $email, $registeredAt, $expiresAt)
            ON CONFLICT(Id) DO UPDATE SET
                Name = excluded.Name,
                Document = excluded.Document,
                Phone = excluded.Phone,
                Email = excluded.Email,
                ExpiresAt = excluded.ExpiresAt;
            """;
        command.Parameters.AddWithValue("$id", customer.Id.ToString());
        command.Parameters.AddWithValue("$name", customer.Name);
        command.Parameters.AddWithValue("$document", customer.Document);
        command.Parameters.AddWithValue("$phone", customer.Phone);
        command.Parameters.AddWithValue("$email", customer.Email);
        command.Parameters.AddWithValue("$registeredAt", customer.RegisteredAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$expiresAt", customer.ExpiresAt.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RenewAsync(Guid customerId, DateTimeOffset renewedAt, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Customers SET RegisteredAt = $renewedAt, ExpiresAt = $expiresAt
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$renewedAt", renewedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$expiresAt", renewedAt.AddDays(30).ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$id", customerId.ToString());
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("O cliente selecionado não foi encontrado.");
    }

    private static string EscapeLike(string value) =>
        value.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);
}
