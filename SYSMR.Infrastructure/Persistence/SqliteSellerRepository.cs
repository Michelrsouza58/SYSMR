using SYSMR.Application.Abstractions;
using SYSMR.Application.Models;
using Microsoft.Data.Sqlite;

namespace SYSMR.Infrastructure.Persistence;

public sealed class SqliteSellerRepository(SqliteDatabase database) : ISellerRepository
{
    public async Task<IReadOnlyList<SellerRecord>> GetAllAsync(
        bool activeOnly = false,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Name, IsActive
            FROM Sellers
            WHERE $activeOnly = 0 OR IsActive = 1
            ORDER BY IsActive DESC, Name COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$activeOnly", activeOnly ? 1 : 0);

        var sellers = new List<SellerRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            sellers.Add(new SellerRecord(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetInt64(2) != 0));
        return sellers;
    }

    public async Task SaveAsync(SellerRecord seller, CancellationToken cancellationToken = default)
    {
        var name = seller.Name.Trim();
        if (name.Length is < 2 or > 100)
            throw new ArgumentException("O nome do vendedor deve ter entre 2 e 100 caracteres.", nameof(seller));

        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Sellers (Id, Name, IsActive)
            VALUES ($id, $name, $isActive)
            ON CONFLICT(Id) DO UPDATE SET
                Name = excluded.Name,
                IsActive = excluded.IsActive;
            """;
        command.Parameters.AddWithValue("$id", seller.Id.ToString());
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$isActive", seller.IsActive ? 1 : 0);
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException($"Já existe um vendedor cadastrado com o nome '{name}'.", exception);
        }
    }
}