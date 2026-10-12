using System.Globalization;
using Microsoft.Data.Sqlite;
using SYSMR.Application.Abstractions;
using SYSMR.Application.Models;
using SYSMR.Domain.Entities;

namespace SYSMR.Infrastructure.Persistence;

public sealed class SqliteSaleRepository(SqliteDatabase database) : ISaleRepository
{
    public async Task SaveAsync(Sale sale, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        foreach (var item in sale.Items)
        {
            await using var readStock = connection.CreateCommand();
            readStock.Transaction = (SqliteTransaction)transaction;
            readStock.CommandText = "SELECT Stock FROM Products WHERE Id = $productId;";
            readStock.Parameters.AddWithValue("$productId", item.ProductId.ToString());
            var stockValue = await readStock.ExecuteScalarAsync(cancellationToken);
            if (stockValue is not string storedStock ||
                decimal.Parse(storedStock, CultureInfo.InvariantCulture) < item.Quantity)
                throw new InvalidOperationException($"Estoque insuficiente ou produto removido: {item.ProductName}.");

            var updatedStock = decimal.Parse(storedStock, CultureInfo.InvariantCulture) - item.Quantity;
            await using var stock = connection.CreateCommand();
            stock.Transaction = (SqliteTransaction)transaction;
            stock.CommandText = "UPDATE Products SET Stock = $stock WHERE Id = $productId;";
            stock.Parameters.AddWithValue("$stock", updatedStock.ToString(CultureInfo.InvariantCulture));
            stock.Parameters.AddWithValue("$productId", item.ProductId.ToString());
            if (await stock.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException($"Estoque insuficiente ou produto removido: {item.ProductName}.");
            await SqliteSyncOutboxRepository.EnqueueAsync(
                connection,
                (SqliteTransaction)transaction,
                "sales",
                "product-stock",
                item.ProductId.ToString(),
                new { productId = item.ProductId, stock = updatedStock },
                cancellationToken);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO Sales (Id, CreatedAt, PaymentMethod, Total, CustomerDocument, CustomerId, SellerName, SyncStatus)
                VALUES ($id, $createdAt, $payment, $total, $document, $customerId, $seller, 0);
                """;
            command.Parameters.AddWithValue("$id", sale.Id.ToString());
            command.Parameters.AddWithValue("$createdAt", sale.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$payment", sale.PaymentMethod.ToString());
            command.Parameters.AddWithValue("$total", sale.Total.ToString(CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$document", (object?)sale.CustomerDocument ?? DBNull.Value);
            command.Parameters.AddWithValue("$customerId", (object?)sale.CustomerId?.ToString() ?? DBNull.Value);
            command.Parameters.AddWithValue("$seller", sale.SellerName);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var item in sale.Items)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO SaleItems (SaleId, ProductId, ProductName, Quantity, UnitPrice, Total)
                VALUES ($saleId, $productId, $name, $quantity, $unitPrice, $total);
                """;
            command.Parameters.AddWithValue("$saleId", sale.Id.ToString());
            command.Parameters.AddWithValue("$productId", item.ProductId.ToString());
            command.Parameters.AddWithValue("$name", item.ProductName);
            command.Parameters.AddWithValue("$quantity", item.Quantity.ToString(CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$unitPrice", item.UnitPrice.ToString(CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$total", item.Total.ToString(CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var cashMovement = connection.CreateCommand())
        {
            cashMovement.Transaction = (SqliteTransaction)transaction;
            cashMovement.CommandText = """
                INSERT INTO CashMovements (Id, CreatedAt, Type, PaymentMethod, Amount, ReferenceId)
                VALUES ($id, $createdAt, 'Sale', $payment, $amount, $reference);
                """;
            cashMovement.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
            cashMovement.Parameters.AddWithValue("$createdAt", sale.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
            cashMovement.Parameters.AddWithValue("$payment", sale.PaymentMethod.ToString());
            cashMovement.Parameters.AddWithValue("$amount", sale.Total.ToString(CultureInfo.InvariantCulture));
            cashMovement.Parameters.AddWithValue("$reference", sale.Id.ToString());
            await cashMovement.ExecuteNonQueryAsync(cancellationToken);
        }

        await RecordAuditAsync(
            connection,
            (SqliteTransaction)transaction,
            sale.CreatedAt,
            sale.SellerName,
            "Venda concluída",
            $"Venda {sale.Id}; total {sale.Total.ToString(CultureInfo.InvariantCulture)}; {sale.Items.Count} linha(s).",
            cancellationToken);
        await SqliteSyncOutboxRepository.EnqueueAsync(
            connection,
            (SqliteTransaction)transaction,
            "sales",
            "sale",
            sale.Id.ToString(),
            new
            {
                sale.Id,
                createdAt = sale.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
                paymentMethod = sale.PaymentMethod.ToString(),
                sale.Total,
                sale.CustomerDocument,
                customerId = sale.CustomerId?.ToString(),
                sale.SellerName,
                items = sale.Items.Select(item => new
                {
                    productId = item.ProductId.ToString(),
                    item.ProductName,
                    item.Quantity,
                    item.UnitPrice,
                    item.Total
                })
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<decimal> GetSalesTotalAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Total FROM Sales;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var total = 0m;
        while (await reader.ReadAsync(cancellationToken))
            total += decimal.Parse(reader.GetString(0), CultureInfo.InvariantCulture);
        await reader.DisposeAsync();
        await using var returns = connection.CreateCommand();
        returns.CommandText = "SELECT CAST(Amount AS TEXT) FROM Returns;";
        await using var returnReader = await returns.ExecuteReaderAsync(cancellationToken);
        while (await returnReader.ReadAsync(cancellationToken))
            total -= decimal.Parse(returnReader.GetString(0), CultureInfo.InvariantCulture);
        return total;
    }

    public async Task<int> GetSalesCountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Sales;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<SaleHistoryEntry>> GetSalesAsync(string query, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.Id, s.CreatedAt, s.PaymentMethod,
                   CAST(s.Total AS TEXT), COALESCE(c.Name, s.CustomerDocument, 'Consumidor final'), s.SellerName,
                   ROUND(COALESCE((SELECT SUM(CAST(r.Amount AS REAL)) FROM Returns r WHERE r.SaleId = s.Id), 0), 2) >= ROUND(CAST(s.Total AS REAL), 2),
                   EXISTS (SELECT 1 FROM ReturnItems ri JOIN Returns r ON r.Id = ri.ReturnId WHERE r.SaleId = s.Id)
            FROM Sales s
            LEFT JOIN Customers c ON c.Id = s.CustomerId
            WHERE $query = ''
               OR s.Id LIKE $pattern ESCAPE '\'
               OR COALESCE(c.Name, '') LIKE $pattern ESCAPE '\'
               OR COALESCE(s.CustomerDocument, '') LIKE $pattern ESCAPE '\'
               OR s.SellerName LIKE $pattern ESCAPE '\'
            ORDER BY s.CreatedAt DESC LIMIT 500;
            """;
        command.Parameters.AddWithValue("$query", query.Trim());
        command.Parameters.AddWithValue("$pattern", $"%{EscapeLike(query.Trim())}%");
        var entries = new List<SaleHistoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new SaleHistoryEntry(
                Guid.Parse(reader.GetString(0)),
                DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                Enum.Parse<PaymentMethod>(reader.GetString(2)),
                decimal.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetInt64(6) != 0)
            {
                HasAnyReturn = reader.GetInt64(7) != 0
            });
        }
        return entries;
    }

    public async Task<SaleRecord?> GetSaleAsync(Guid saleId, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var header = connection.CreateCommand();
        header.CommandText = """
            SELECT s.Id, s.CreatedAt, s.PaymentMethod, s.Total,
                   COALESCE(c.Name, s.CustomerDocument, 'Consumidor final'), s.SellerName,
                   ROUND(COALESCE((SELECT SUM(CAST(r.Amount AS REAL)) FROM Returns r WHERE r.SaleId = s.Id), 0), 2) >= ROUND(CAST(s.Total AS REAL), 2)
            FROM Sales s LEFT JOIN Customers c ON c.Id = s.CustomerId
            WHERE s.Id = $id;
            """;
        header.Parameters.AddWithValue("$id", saleId.ToString());
        await using var reader = await header.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        var summary = new SaleHistoryEntry(
            Guid.Parse(reader.GetString(0)),
            DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
            Enum.Parse<PaymentMethod>(reader.GetString(2)),
            decimal.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt64(6) != 0);
        await reader.DisposeAsync();

        await using var itemsCommand = connection.CreateCommand();
        itemsCommand.CommandText = """
            SELECT ProductId, ProductName, Quantity, UnitPrice
            FROM SaleItems WHERE SaleId = $saleId ORDER BY Id;
            """;
        itemsCommand.Parameters.AddWithValue("$saleId", saleId.ToString());
        var items = new List<SaleItem>();
        await using var itemReader = await itemsCommand.ExecuteReaderAsync(cancellationToken);
        while (await itemReader.ReadAsync(cancellationToken))
        {
            items.Add(new SaleItem(
                Guid.Parse(itemReader.GetString(0)),
                itemReader.GetString(1),
                decimal.Parse(itemReader.GetString(2), CultureInfo.InvariantCulture),
                decimal.Parse(itemReader.GetString(3), CultureInfo.InvariantCulture)));
        }
        await using var returnedCommand = connection.CreateCommand();
        returnedCommand.CommandText = """
            SELECT ProductId, SUM(CAST(Quantity AS REAL))
            FROM ReturnItems
            WHERE ReturnId IN (SELECT Id FROM Returns WHERE SaleId = $saleId)
            GROUP BY ProductId;
            """;
        returnedCommand.Parameters.AddWithValue("$saleId", saleId.ToString());
        var returnedQuantities = new Dictionary<Guid, decimal>();
        await using (var returnedReader = await returnedCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await returnedReader.ReadAsync(cancellationToken))
                returnedQuantities[Guid.Parse(returnedReader.GetString(0))] =
                    Convert.ToDecimal(returnedReader.GetDouble(1), CultureInfo.InvariantCulture);
        }
        return new SaleRecord(summary, items, returnedQuantities);
    }

    public async Task ReturnSaleAsync(
        Guid saleId,
        IReadOnlyCollection<SaleItem> requestedItems,
        DateTimeOffset returnedAt,
        string actor,
        CancellationToken cancellationToken = default)
    {
        if (requestedItems.Count == 0 || requestedItems.Any(item => item.Quantity <= 0))
            throw new ArgumentException("Selecione ao menos um item com quantidade válida para devolver.", nameof(requestedItems));

        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using var saleCommand = connection.CreateCommand();
        saleCommand.Transaction = (SqliteTransaction)transaction;
        saleCommand.CommandText = "SELECT PaymentMethod, Total FROM Sales WHERE Id = $id;";
        saleCommand.Parameters.AddWithValue("$id", saleId.ToString());
        string paymentText;
        decimal saleTotal;
        await using (var saleReader = await saleCommand.ExecuteReaderAsync(cancellationToken))
        {
            if (!await saleReader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("A venda selecionada não foi encontrada.");
            paymentText = saleReader.GetString(0);
            saleTotal = decimal.Parse(saleReader.GetString(1), CultureInfo.InvariantCulture);
        }
        if (string.IsNullOrWhiteSpace(paymentText))
            throw new InvalidOperationException("A venda selecionada não foi encontrada.");
        var paymentMethod = Enum.Parse<PaymentMethod>(paymentText);

        await using var itemsCommand = connection.CreateCommand();
        itemsCommand.Transaction = (SqliteTransaction)transaction;
        itemsCommand.CommandText = """
            SELECT ProductId, ProductName, Quantity, UnitPrice, Total
            FROM SaleItems WHERE SaleId = $saleId ORDER BY Id;
            """;
        itemsCommand.Parameters.AddWithValue("$saleId", saleId.ToString());
        var soldItems = new Dictionary<Guid, (string Name, decimal Quantity, decimal UnitPrice)>();
        await using (var reader = await itemsCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var productId = Guid.Parse(reader.GetString(0));
                var quantity = decimal.Parse(reader.GetString(2), CultureInfo.InvariantCulture);
                var unitPrice = decimal.Parse(reader.GetString(3), CultureInfo.InvariantCulture);
                if (soldItems.TryGetValue(productId, out var existing))
                    soldItems[productId] = (existing.Name, existing.Quantity + quantity, unitPrice);
                else
                    soldItems[productId] = (reader.GetString(1), quantity, unitPrice);
            }
        }
        if (soldItems.Count == 0)
            throw new InvalidOperationException("A venda não possui itens para devolver.");

        var priorReturned = new Dictionary<Guid, decimal>();
        await using (var returnedItemsCommand = connection.CreateCommand())
        {
            returnedItemsCommand.Transaction = (SqliteTransaction)transaction;
            returnedItemsCommand.CommandText = """
                SELECT ri.ProductId, SUM(CAST(ri.Quantity AS REAL))
                FROM ReturnItems ri
                JOIN Returns r ON r.Id = ri.ReturnId
                WHERE r.SaleId = $saleId
                GROUP BY ri.ProductId;
                """;
            returnedItemsCommand.Parameters.AddWithValue("$saleId", saleId.ToString());
            await using var reader = await returnedItemsCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                priorReturned[Guid.Parse(reader.GetString(0))] =
                    Convert.ToDecimal(reader.GetDouble(1), CultureInfo.InvariantCulture);
        }

        var normalizedItems = new Dictionary<Guid, (string Name, decimal Quantity, decimal UnitPrice, decimal Total)>();
        foreach (var requested in requestedItems)
        {
            if (!soldItems.TryGetValue(requested.ProductId, out var sold))
                throw new InvalidOperationException($"O produto '{requested.ProductName}' não pertence a esta venda.");
            var alreadyReturned = priorReturned.GetValueOrDefault(requested.ProductId);
            var inThisReturn = normalizedItems.GetValueOrDefault(requested.ProductId).Quantity;
            if (alreadyReturned + inThisReturn + requested.Quantity > sold.Quantity)
                throw new InvalidOperationException(
                    $"A quantidade disponível para devolver de '{sold.Name}' é {sold.Quantity - alreadyReturned - inThisReturn:N3}.");
            normalizedItems[requested.ProductId] = (
                sold.Name,
                inThisReturn + requested.Quantity,
                sold.UnitPrice,
                decimal.Round((inThisReturn + requested.Quantity) * sold.UnitPrice, 2, MidpointRounding.AwayFromZero));
        }

        var items = normalizedItems.Select(item =>
            (ProductId: item.Key, item.Value.Name, item.Value.Quantity, item.Value.UnitPrice, item.Value.Total)).ToList();
        var amount = items.Sum(item => item.Total);
        if (amount <= 0)
            throw new InvalidOperationException("O valor da devolução precisa ser maior que zero.");

        foreach (var item in items)
        {
            await using var stockQuery = connection.CreateCommand();
            stockQuery.Transaction = (SqliteTransaction)transaction;
            stockQuery.CommandText = "SELECT Stock FROM Products WHERE Id = $productId;";
            stockQuery.Parameters.AddWithValue("$productId", item.ProductId.ToString());
            var stockValue = await stockQuery.ExecuteScalarAsync(cancellationToken);
            if (stockValue is not string stockText)
                throw new InvalidOperationException($"O produto '{item.Name}' não existe mais no estoque.");
            var updatedStock = decimal.Parse(stockText, CultureInfo.InvariantCulture) + item.Quantity;

            await using var updateStock = connection.CreateCommand();
            updateStock.Transaction = (SqliteTransaction)transaction;
            updateStock.CommandText = "UPDATE Products SET Stock = $stock WHERE Id = $productId;";
            updateStock.Parameters.AddWithValue("$stock", updatedStock.ToString(CultureInfo.InvariantCulture));
            updateStock.Parameters.AddWithValue("$productId", item.ProductId.ToString());
            await updateStock.ExecuteNonQueryAsync(cancellationToken);
            await SqliteSyncOutboxRepository.EnqueueAsync(
                connection,
                (SqliteTransaction)transaction,
                "sales",
                "product-stock",
                item.ProductId.ToString(),
                new { productId = item.ProductId, stock = updatedStock },
                cancellationToken);
        }

        var returnId = Guid.NewGuid();
        await using (var insertReturn = connection.CreateCommand())
        {
            insertReturn.Transaction = (SqliteTransaction)transaction;
            insertReturn.CommandText = """
                INSERT INTO Returns (Id, SaleId, CreatedAt, Amount, PaymentMethod, SyncStatus)
                VALUES ($id, $saleId, $createdAt, $amount, $payment, 0)
                ON CONFLICT(SaleId) DO UPDATE SET
                    CreatedAt = excluded.CreatedAt,
                    Amount = printf('%.2f', CAST(Returns.Amount AS REAL) + CAST(excluded.Amount AS REAL));
                """;
            insertReturn.Parameters.AddWithValue("$id", returnId.ToString());
            insertReturn.Parameters.AddWithValue("$saleId", saleId.ToString());
            insertReturn.Parameters.AddWithValue("$createdAt", returnedAt.ToString("O", CultureInfo.InvariantCulture));
            insertReturn.Parameters.AddWithValue("$amount", amount.ToString(CultureInfo.InvariantCulture));
            insertReturn.Parameters.AddWithValue("$payment", paymentMethod.ToString());
            await insertReturn.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var returnIdCommand = connection.CreateCommand())
        {
            returnIdCommand.Transaction = (SqliteTransaction)transaction;
            returnIdCommand.CommandText = "SELECT Id FROM Returns WHERE SaleId = $saleId;";
            returnIdCommand.Parameters.AddWithValue("$saleId", saleId.ToString());
            returnId = Guid.Parse((string)(await returnIdCommand.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("Não foi possível localizar o registro da devolução.")));
        }

        foreach (var item in items)
        {
            await using var insertItem = connection.CreateCommand();
            insertItem.Transaction = (SqliteTransaction)transaction;
            insertItem.CommandText = """
                INSERT INTO ReturnItems (ReturnId, ProductId, ProductName, Quantity, UnitPrice, Total)
                VALUES ($returnId, $productId, $name, $quantity, $unitPrice, $total);
                """;
            insertItem.Parameters.AddWithValue("$returnId", returnId.ToString());
            insertItem.Parameters.AddWithValue("$productId", item.ProductId.ToString());
            insertItem.Parameters.AddWithValue("$name", item.Name);
            insertItem.Parameters.AddWithValue("$quantity", item.Quantity.ToString(CultureInfo.InvariantCulture));
            insertItem.Parameters.AddWithValue("$unitPrice", item.UnitPrice.ToString(CultureInfo.InvariantCulture));
            insertItem.Parameters.AddWithValue("$total", item.Total.ToString(CultureInfo.InvariantCulture));
            await insertItem.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var movement = connection.CreateCommand())
        {
            movement.Transaction = (SqliteTransaction)transaction;
            movement.CommandText = """
                INSERT INTO CashMovements (Id, CreatedAt, Type, PaymentMethod, Amount, ReferenceId)
                VALUES ($id, $createdAt, 'Refund', $payment, $amount, $reference);
                """;
            movement.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
            movement.Parameters.AddWithValue("$createdAt", returnedAt.ToString("O", CultureInfo.InvariantCulture));
            movement.Parameters.AddWithValue("$payment", paymentMethod.ToString());
            movement.Parameters.AddWithValue("$amount", (-amount).ToString(CultureInfo.InvariantCulture));
            movement.Parameters.AddWithValue("$reference", returnId.ToString());
            await movement.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var totalReturnedCommand = connection.CreateCommand();
        totalReturnedCommand.Transaction = (SqliteTransaction)transaction;
        totalReturnedCommand.CommandText = "SELECT CAST(Amount AS TEXT) FROM Returns WHERE SaleId = $saleId;";
        totalReturnedCommand.Parameters.AddWithValue("$saleId", saleId.ToString());
        var totalReturnedText = (string?)await totalReturnedCommand.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("Não foi possível confirmar o total devolvido.");
        var action = decimal.Parse(totalReturnedText, CultureInfo.InvariantCulture) >= saleTotal
            ? "Devolução integral"
            : "Devolução parcial";

        await RecordAuditAsync(
            connection,
            (SqliteTransaction)transaction,
            returnedAt,
            actor,
            action,
            $"Venda {saleId}; devolução {returnId}; {items.Count} item(ns); valor {amount.ToString(CultureInfo.InvariantCulture)}.",
            cancellationToken);
        await SqliteSyncOutboxRepository.EnqueueAsync(
            connection,
            (SqliteTransaction)transaction,
            "sales",
            "return",
            returnId.ToString(),
            new
            {
                id = returnId,
                saleId,
                createdAt = returnedAt.ToString("O", CultureInfo.InvariantCulture),
                amount,
                paymentMethod = paymentMethod.ToString(),
                items = items.Select(item => new
                {
                    productId = item.ProductId.ToString(),
                    productName = item.Name,
                    item.Quantity,
                    item.UnitPrice,
                    item.Total
                })
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task RecordAuditAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset timestamp,
        string actor,
        string action,
        string details,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO AuditLog (Id, CreatedAt, Actor, Action, Details)
            VALUES ($id, $createdAt, $actor, $action, $details);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("$createdAt", timestamp.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$details", details);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string EscapeLike(string value) =>
        value.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);
}
