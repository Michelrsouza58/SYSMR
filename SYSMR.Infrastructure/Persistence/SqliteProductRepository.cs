using System.Globalization;
using Microsoft.Data.Sqlite;
using SYSMR.Application.Abstractions;
using SYSMR.Domain.Entities;

namespace SYSMR.Infrastructure.Persistence;

public sealed class SqliteProductRepository(SqliteDatabase database, string branch = "sales") : IProductRepository
{
    private readonly string _branch = branch is "sales" or "restaurant"
        ? branch
        : throw new ArgumentException("A vertente da conta é inválida.", nameof(branch));

    public Task<Product?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        FindAsync("Id = $code", id.ToString(), cancellationToken);

    public Task<Product?> FindByBarcodeAsync(string barcode, CancellationToken cancellationToken = default) =>
        FindAsync("Barcode = $code", barcode, cancellationToken);

    public Task<Product?> FindByScaleCodeAsync(string scaleCode, CancellationToken cancellationToken = default) =>
        FindAsync("ScaleCode = $code", scaleCode, cancellationToken);

    public async Task<IReadOnlyList<Product>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Name, Barcode, ScaleCode, CostPrice, SalePrice, Stock, MinimumStock, Category, Ncm, Cest, SoldByWeight, UnitOfMeasure
            FROM Products
            WHERE $query = '' OR Name LIKE $pattern ESCAPE '\' OR Barcode LIKE $pattern ESCAPE '\' OR Category LIKE $pattern ESCAPE '\'
            ORDER BY Name LIMIT 100;
            """;
        command.Parameters.AddWithValue("$query", query.Trim());
        command.Parameters.AddWithValue("$pattern", $"%{EscapeLike(query.Trim())}%");
        var products = new List<Product>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            products.Add(Map(reader));
        return products;
    }

    public async Task<IReadOnlyList<Product>> SearchLowStockAsync(decimal maximumQuantity, CancellationToken cancellationToken = default)
    {
        if (maximumQuantity < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumQuantity));
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Name, Barcode, ScaleCode, CostPrice, SalePrice, Stock, MinimumStock, Category, Ncm, Cest, SoldByWeight, UnitOfMeasure
            FROM Products ORDER BY Name;
            """;
        var products = new List<Product>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var product = Map(reader);
            if (product.Stock <= maximumQuantity)
                products.Add(product);
        }
        products.Sort((left, right) =>
        {
            var quantityComparison = left.Stock.CompareTo(right.Stock);
            return quantityComparison != 0
                ? quantityComparison
                : StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name);
        });
        return products;
    }

    public async Task SaveAsync(Product product, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO Products
                (Id, Name, Barcode, ScaleCode, CostPrice, SalePrice, Stock, MinimumStock, Category, Ncm, Cest, SoldByWeight, UnitOfMeasure)
            VALUES
                ($id, $name, $barcode, $scaleCode, $costPrice, $salePrice, $stock, $minimumStock, $category, $ncm, $cest, $soldByWeight, $unit)
            ON CONFLICT(Id) DO UPDATE SET
                Name = excluded.Name,
                Barcode = excluded.Barcode,
                ScaleCode = excluded.ScaleCode,
                CostPrice = excluded.CostPrice,
                SalePrice = excluded.SalePrice,
                Stock = excluded.Stock,
                MinimumStock = excluded.MinimumStock,
                Category = excluded.Category,
                Ncm = excluded.Ncm,
                Cest = excluded.Cest,
                SoldByWeight = excluded.SoldByWeight,
                UnitOfMeasure = excluded.UnitOfMeasure;
            """;
        command.Parameters.AddWithValue("$id", product.Id.ToString());
        command.Parameters.AddWithValue("$name", product.Name);
        command.Parameters.AddWithValue("$barcode", product.Barcode);
        command.Parameters.AddWithValue("$scaleCode", product.ScaleCode);
        command.Parameters.AddWithValue("$costPrice", product.CostPrice.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$salePrice", product.SalePrice.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$stock", product.Stock.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$minimumStock", product.MinimumStock.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$category", product.Category);
        command.Parameters.AddWithValue("$ncm", product.Ncm);
        command.Parameters.AddWithValue("$cest", product.Cest);
        command.Parameters.AddWithValue("$soldByWeight", product.SoldByWeight ? 1 : 0);
        command.Parameters.AddWithValue("$unit", product.UnitOfMeasure);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await SqliteSyncOutboxRepository.EnqueueAsync(
            connection,
            (SqliteTransaction)transaction,
            _branch,
            "product",
            product.Id.ToString(),
            new
            {
                id = product.Id,
                product.Name,
                product.Barcode,
                product.ScaleCode,
                product.CostPrice,
                product.SalePrice,
                product.Stock,
                product.MinimumStock,
                product.Category,
                product.Ncm,
                product.Cest,
                product.SoldByWeight,
                product.UnitOfMeasure
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<Product?> FindAsync(string predicate, string code, CancellationToken cancellationToken)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT Id, Name, Barcode, ScaleCode, CostPrice, SalePrice, Stock, MinimumStock, Category, Ncm, Cest, SoldByWeight, UnitOfMeasure
            FROM Products WHERE {predicate} LIMIT 1;
            """;
        command.Parameters.AddWithValue("$code", code);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
    }

    internal static Product Map(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        decimal.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
        decimal.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
        decimal.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
        decimal.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
        reader.GetString(8),
        reader.GetString(9),
        reader.GetString(10),
        reader.GetInt64(11) != 0,
        reader.GetString(12));

    private static string EscapeLike(string value) =>
        value.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);
}
