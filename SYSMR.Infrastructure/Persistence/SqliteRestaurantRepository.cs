using System.Globalization;
using SYSMR.Application.Models;
using SYSMR.Domain.Entities;
using Microsoft.Data.Sqlite;

namespace SYSMR.Infrastructure.Persistence;

public sealed class SqliteRestaurantRepository(SqliteDatabase database)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO RestaurantAreas (Id, Name, SortOrder)
            VALUES ($id, 'Salão', 0);
            INSERT OR IGNORE INTO AppSettings (Key, Value)
            VALUES ('restaurant.tables.defaultCount', '12');
            WITH candidates AS (
                SELECT a.Id AS AreaId, lower(hex(randomblob(16))) AS RawId, n.Number
                FROM RestaurantAreas a
                JOIN (SELECT 1 AS Number UNION SELECT 2 UNION SELECT 3 UNION SELECT 4
                      UNION SELECT 5 UNION SELECT 6 UNION SELECT 7 UNION SELECT 8
                      UNION SELECT 9 UNION SELECT 10 UNION SELECT 11 UNION SELECT 12) n
                WHERE a.Name = 'Salão'
                  AND NOT EXISTS (SELECT 1 FROM RestaurantTables t WHERE t.AreaId = a.Id)
            )
            INSERT INTO RestaurantTables (Id, AreaId, Number)
            SELECT substr(RawId, 1, 8) || '-' || substr(RawId, 9, 4) || '-' ||
                   substr(RawId, 13, 4) || '-' || substr(RawId, 17, 4) || '-' ||
                   substr(RawId, 21, 12), AreaId, Number
            FROM candidates;
            """;
        command.Parameters.AddWithValue("$id", Guid.Parse("c7493ec0-bca7-49d7-a5a0-f145f507a3ed").ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
        await using var clearEmptyOrders = connection.CreateCommand();
        clearEmptyOrders.CommandText = """
            UPDATE RestaurantOrders
            SET Status = 'Cancelled', ClosedAt = $closedAt
            WHERE Status = 'Open'
              AND NOT EXISTS (SELECT 1 FROM RestaurantOrderItems i WHERE i.OrderId = RestaurantOrders.Id);
            """;
        clearEmptyOrders.Parameters.AddWithValue(
            "$closedAt",
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await clearEmptyOrders.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RestaurantAreaRecord>> GetAreasAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.Id, a.Name, a.SortOrder, COALESCE(SUM(CASE WHEN t.IsActive = 1 THEN 1 ELSE 0 END), 0)
            FROM RestaurantAreas a LEFT JOIN RestaurantTables t ON t.AreaId = a.Id
            GROUP BY a.Id ORDER BY a.SortOrder, a.Name;
            """;
        var result = new List<RestaurantAreaRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new RestaurantAreaRecord(
                Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3)));
        return result;
    }

    public async Task<Guid> SaveAreaAsync(string name, CancellationToken cancellationToken = default)
    {
        name = ValidateName(name, "área");
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var id = Guid.NewGuid();
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO RestaurantAreas (Id, Name, SortOrder)
            VALUES ($id, $name, COALESCE((SELECT MAX(SortOrder) + 1 FROM RestaurantAreas), 0));
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$name", name);
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
            await SqliteSyncOutboxRepository.EnqueueAsync(
                connection,
                (SqliteTransaction)transaction,
                "restaurant",
                "area",
                id.ToString(),
                new { id, name },
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return id;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException($"A área '{name}' já existe.", exception);
        }
    }

    public async Task SetTableCountAsync(Guid areaId, int count, CancellationToken cancellationToken = default)
    {
        if (count is < 1 or > 200)
            throw new ArgumentOutOfRangeException(nameof(count), "A quantidade de mesas deve estar entre 1 e 200.");
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using var verify = connection.CreateCommand();
        verify.Transaction = (SqliteTransaction)transaction;
        verify.CommandText = "SELECT COUNT(*) FROM RestaurantAreas WHERE Id = $areaId;";
        verify.Parameters.AddWithValue("$areaId", areaId.ToString());
        if (Convert.ToInt32(await verify.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 1)
            throw new InvalidOperationException("A área selecionada não existe.");

        await using var currentCommand = connection.CreateCommand();
        currentCommand.Transaction = (SqliteTransaction)transaction;
        currentCommand.CommandText = "SELECT Number, Id FROM RestaurantTables WHERE AreaId = $areaId;";
        currentCommand.Parameters.AddWithValue("$areaId", areaId.ToString());
        var existing = new Dictionary<int, string>();
        await using (var reader = await currentCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                existing.Add(reader.GetInt32(0), reader.GetString(1));
        }

        foreach (var table in existing.Where(table => table.Key > count))
        {
            await using var deactivate = connection.CreateCommand();
            deactivate.Transaction = (SqliteTransaction)transaction;
            deactivate.CommandText = """
                UPDATE RestaurantTables SET IsActive = 0
                WHERE Id = $id
                  AND NOT EXISTS (SELECT 1 FROM RestaurantOrders WHERE TableId = $id AND Status = 'Open');
                """;
            deactivate.Parameters.AddWithValue("$id", table.Value);
            if (await deactivate.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("Feche as comandas abertas antes de reduzir a quantidade de mesas.");
        }

        for (var number = 1; number <= count; number++)
        {
            if (existing.TryGetValue(number, out var existingId))
            {
                await using var activate = connection.CreateCommand();
                activate.Transaction = (SqliteTransaction)transaction;
                activate.CommandText = "UPDATE RestaurantTables SET IsActive = 1 WHERE Id = $id;";
                activate.Parameters.AddWithValue("$id", existingId);
                await activate.ExecuteNonQueryAsync(cancellationToken);
                continue;
            }

            await using var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = "INSERT INTO RestaurantTables (Id, AreaId, Number) VALUES ($id, $areaId, $number);";
            insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
            insert.Parameters.AddWithValue("$areaId", areaId.ToString());
            insert.Parameters.AddWithValue("$number", number);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await SqliteSyncOutboxRepository.EnqueueAsync(
            connection,
            (SqliteTransaction)transaction,
            "restaurant",
            "area-tables",
            areaId.ToString(),
            new { areaId, tableCount = count },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RestaurantTableRecord>> GetTablesAsync(Guid areaId, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.Id, t.AreaId, t.Number, COALESCE(o.Id, ''), ROUND(COALESCE((
                SELECT SUM(CAST(i.Quantity AS REAL) * CAST(i.UnitPrice AS REAL))
                FROM RestaurantOrderItems i WHERE i.OrderId = o.Id), 0) *
                (1 + CASE WHEN COALESCE(o.WaiterServiceChargeEnabled, 0) = 1 THEN 0.10 ELSE 0 END), 2)
            FROM RestaurantTables t
            LEFT JOIN RestaurantOrders o ON o.TableId = t.Id AND o.Status = 'Open'
            WHERE t.AreaId = $areaId AND t.IsActive = 1
            ORDER BY t.Number;
            """;
        command.Parameters.AddWithValue("$areaId", areaId.ToString());
        var result = new List<RestaurantTableRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new RestaurantTableRecord(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetInt32(2),
                reader.GetString(3).Length > 0,
                Convert.ToDecimal(reader.GetDouble(4), CultureInfo.InvariantCulture)));
        return result;
    }

    public async Task<IReadOnlyList<RestaurantMenuItemRecord>> GetMenuItemsAsync(bool activeOnly = false, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Name, Category, Price, IsActive, InventoryProductId, InventoryQuantityPerUnit
            FROM RestaurantMenuItems
            WHERE $activeOnly = 0 OR IsActive = 1 ORDER BY IsActive DESC, Category, Name;
            """;
        command.Parameters.AddWithValue("$activeOnly", activeOnly ? 1 : 0);
        var result = new List<RestaurantMenuItemRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new RestaurantMenuItemRecord(
                Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
                decimal.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                reader.GetInt64(4) != 0,
                reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5)),
                decimal.Parse(reader.GetString(6), CultureInfo.InvariantCulture)));
        return result;
    }

    public async Task SaveMenuItemAsync(RestaurantMenuItemRecord item, CancellationToken cancellationToken = default)
    {
        var name = ValidateName(item.Name, "item");
        if (item.Price <= 0)
            throw new ArgumentOutOfRangeException(nameof(item), "O preço do item deve ser maior que zero.");
        if (item.InventoryQuantityPerUnit < 0 ||
            (item.InventoryProductId is null && item.InventoryQuantityPerUnit != 0) ||
            (item.InventoryProductId is not null && item.InventoryQuantityPerUnit <= 0))
            throw new ArgumentOutOfRangeException(nameof(item), "Informe um produto e uma quantidade de estoque válida por unidade.");
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO RestaurantMenuItems
                (Id, Name, Category, Price, IsActive, InventoryProductId, InventoryQuantityPerUnit)
            VALUES ($id, $name, $category, $price, $active, $productId, $quantity)
            ON CONFLICT(Id) DO UPDATE SET Name=excluded.Name, Category=excluded.Category,
                Price=excluded.Price, IsActive=excluded.IsActive,
                InventoryProductId=excluded.InventoryProductId,
                InventoryQuantityPerUnit=excluded.InventoryQuantityPerUnit;
            """;
        command.Parameters.AddWithValue("$id", item.Id.ToString());
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$category", item.Category.Trim());
        command.Parameters.AddWithValue("$price", item.Price.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$active", item.IsActive ? 1 : 0);
        command.Parameters.AddWithValue("$productId", item.InventoryProductId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$quantity", item.InventoryQuantityPerUnit.ToString(CultureInfo.InvariantCulture));
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
            await SqliteSyncOutboxRepository.EnqueueAsync(
                connection,
                (SqliteTransaction)transaction,
                "restaurant",
                "menu-item",
                item.Id.ToString(),
                new
                {
                    id = item.Id,
                    name,
                    item.Category,
                    item.Price,
                    item.IsActive,
                    item.InventoryProductId,
                    item.InventoryQuantityPerUnit
                },
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException($"O item '{name}' já existe no cardápio.", exception);
        }
    }

    public async Task<IReadOnlyList<RestaurantEmployeeRecord>> GetEmployeesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name, Role, IsActive FROM RestaurantEmployees ORDER BY IsActive DESC, Name;";
        var result = new List<RestaurantEmployeeRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new RestaurantEmployeeRecord(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetInt64(3) != 0));
        return result;
    }

    public async Task SaveEmployeeAsync(RestaurantEmployeeRecord employee, CancellationToken cancellationToken = default)
    {
        var name = ValidateName(employee.Name, "funcionário");
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO RestaurantEmployees (Id, Name, Role, IsActive)
            VALUES ($id, $name, $role, $active)
            ON CONFLICT(Id) DO UPDATE SET Name=excluded.Name, Role=excluded.Role, IsActive=excluded.IsActive;
            """;
        command.Parameters.AddWithValue("$id", employee.Id.ToString());
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$role", string.IsNullOrWhiteSpace(employee.Role) ? "Atendente" : employee.Role.Trim());
        command.Parameters.AddWithValue("$active", employee.IsActive ? 1 : 0);
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
            await SqliteSyncOutboxRepository.EnqueueAsync(
                connection,
                (SqliteTransaction)transaction,
                "restaurant",
                "employee",
                employee.Id.ToString(),
                new { employee.Id, name, employee.Role, employee.IsActive },
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException($"O funcionário '{name}' já está cadastrado.", exception);
        }
    }

    public async Task<Guid> OpenTableAsync(Guid tableId, Guid? employeeId, CancellationToken cancellationToken = default)
    {
        var orderId = Guid.NewGuid();
        var openedAt = DateTimeOffset.UtcNow;
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO RestaurantOrders (Id, TableId, EmployeeId, OpenedAt, Status)
            SELECT $id, t.Id, $employeeId, $openedAt, 'Open'
            FROM RestaurantTables t WHERE t.Id = $tableId;
            """;
        command.Parameters.AddWithValue("$id", orderId.ToString());
        command.Parameters.AddWithValue("$tableId", tableId.ToString());
        command.Parameters.AddWithValue("$employeeId", employeeId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$openedAt", openedAt.ToString("O", CultureInfo.InvariantCulture));
        try
        {
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("A mesa selecionada não existe.");
            await SqliteSyncOutboxRepository.EnqueueAsync(
                connection,
                (SqliteTransaction)transaction,
                "restaurant",
                "order",
                orderId.ToString(),
                new { orderId, tableId, employeeId, openedAt = openedAt.ToString("O", CultureInfo.InvariantCulture) },
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return orderId;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException("A mesa já está ocupada por uma comanda aberta.", exception);
        }
    }

    public async Task<RestaurantOrderRecord?> GetOpenOrderAsync(Guid tableId, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var header = connection.CreateCommand();
        header.CommandText = """
            SELECT o.Id, o.TableId, COALESCE(e.Name, ''), o.OpenedAt,
                   COALESCE(o.WaiterServiceChargeEnabled, 0)
            FROM RestaurantOrders o LEFT JOIN RestaurantEmployees e ON e.Id = o.EmployeeId
            WHERE o.TableId = $tableId AND o.Status = 'Open';
            """;
        header.Parameters.AddWithValue("$tableId", tableId.ToString());
        await using var reader = await header.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        var id = Guid.Parse(reader.GetString(0));
        var table = Guid.Parse(reader.GetString(1));
        var employee = reader.GetString(2);
        var openedAt = DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture);
        var waiterServiceChargeEnabled = reader.GetInt64(4) != 0;
        await reader.DisposeAsync();

        await using var itemsCommand = connection.CreateCommand();
        itemsCommand.CommandText = """
            SELECT Id, MenuItemName, Quantity, UnitPrice FROM RestaurantOrderItems
            WHERE OrderId = $orderId ORDER BY rowid;
            """;
        itemsCommand.Parameters.AddWithValue("$orderId", id.ToString());
        var items = new List<RestaurantOrderItemRecord>();
        await using var itemsReader = await itemsCommand.ExecuteReaderAsync(cancellationToken);
        while (await itemsReader.ReadAsync(cancellationToken))
            items.Add(new RestaurantOrderItemRecord(
                Guid.Parse(itemsReader.GetString(0)),
                itemsReader.GetString(1),
                decimal.Parse(itemsReader.GetString(2), CultureInfo.InvariantCulture),
                decimal.Parse(itemsReader.GetString(3), CultureInfo.InvariantCulture)));
        await itemsReader.DisposeAsync();
        await using var paidCommand = connection.CreateCommand();
        paidCommand.CommandText = """
            SELECT COALESCE(SUM(CAST(Amount AS REAL)), 0)
            FROM RestaurantPayments WHERE OrderId = $orderId;
            """;
        paidCommand.Parameters.AddWithValue("$orderId", id.ToString());
        var paidTotal = Convert.ToDecimal(await paidCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        return new RestaurantOrderRecord(id, table, employee, openedAt, items, paidTotal, waiterServiceChargeEnabled);
    }

    public async Task AddOrderItemAsync(Guid tableId, Guid menuItemId, decimal quantity, CancellationToken cancellationToken = default)
    {
        await AddOrderItemAsync(tableId, menuItemId, quantity, null, cancellationToken);
    }

    public async Task AddOrderItemAsync(
        Guid tableId,
        Guid menuItemId,
        decimal quantity,
        Guid? employeeId,
        CancellationToken cancellationToken = default)
    {
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), "A quantidade deve ser maior que zero.");
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        string? orderId;
        string menuName;
        decimal unitPrice;
        string? inventoryProductId;
        decimal inventoryQuantityPerUnit;
        await using (var item = connection.CreateCommand())
        {
            item.Transaction = (SqliteTransaction)transaction;
            item.CommandText = """
                SELECT o.Id, m.Name, m.Price, m.InventoryProductId, m.InventoryQuantityPerUnit
                FROM RestaurantTables t
                JOIN RestaurantMenuItems m ON m.Id = $menuItemId AND m.IsActive = 1
                LEFT JOIN RestaurantOrders o ON o.TableId = t.Id AND o.Status = 'Open'
                WHERE t.Id = $tableId AND t.IsActive = 1;
                """;
            item.Parameters.AddWithValue("$tableId", tableId.ToString());
            item.Parameters.AddWithValue("$menuItemId", menuItemId.ToString());
            await using var reader = await item.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("Não foi possível adicionar: confira se a mesa existe e o item está ativo.");
            orderId = reader.IsDBNull(0) ? null : reader.GetString(0);
            menuName = reader.GetString(1);
            unitPrice = decimal.Parse(reader.GetString(2), CultureInfo.InvariantCulture);
            inventoryProductId = reader.IsDBNull(3) ? null : reader.GetString(3);
            inventoryQuantityPerUnit = decimal.Parse(reader.GetString(4), CultureInfo.InvariantCulture);
        }

        if (orderId is null)
        {
            orderId = Guid.NewGuid().ToString();
            var openedAt = DateTimeOffset.UtcNow;
            await using var createOrder = connection.CreateCommand();
            createOrder.Transaction = (SqliteTransaction)transaction;
            createOrder.CommandText = """
                INSERT INTO RestaurantOrders (Id, TableId, EmployeeId, OpenedAt, Status)
                VALUES ($id, $tableId, $employeeId, $openedAt, 'Open');
                """;
            createOrder.Parameters.AddWithValue("$id", orderId);
            createOrder.Parameters.AddWithValue("$tableId", tableId.ToString());
            createOrder.Parameters.AddWithValue("$employeeId", employeeId?.ToString() ?? (object)DBNull.Value);
            createOrder.Parameters.AddWithValue("$openedAt", openedAt.ToString("O", CultureInfo.InvariantCulture));
            await createOrder.ExecuteNonQueryAsync(cancellationToken);
            await SqliteSyncOutboxRepository.EnqueueAsync(
                connection,
                (SqliteTransaction)transaction,
                "restaurant",
                "order",
                orderId,
                new { orderId, tableId, employeeId, openedAt = openedAt.ToString("O", CultureInfo.InvariantCulture) },
                cancellationToken);
        }
        else if (employeeId is not null)
        {
            await using var assignEmployee = connection.CreateCommand();
            assignEmployee.Transaction = (SqliteTransaction)transaction;
            assignEmployee.CommandText = """
                UPDATE RestaurantOrders SET EmployeeId = $employeeId
                WHERE Id = $orderId
                  AND NOT EXISTS (SELECT 1 FROM RestaurantOrderItems i WHERE i.OrderId = $orderId);
                """;
            assignEmployee.Parameters.AddWithValue("$employeeId", employeeId.Value.ToString());
            assignEmployee.Parameters.AddWithValue("$orderId", orderId);
            await assignEmployee.ExecuteNonQueryAsync(cancellationToken);
        }

        if (inventoryProductId is not null && inventoryQuantityPerUnit > 0)
        {
            var requiredStock = quantity * inventoryQuantityPerUnit;
            await using var stock = connection.CreateCommand();
            stock.Transaction = (SqliteTransaction)transaction;
            stock.CommandText = "SELECT Stock FROM Products WHERE Id = $id;";
            stock.Parameters.AddWithValue("$id", inventoryProductId);
            var stockValue = await stock.ExecuteScalarAsync(cancellationToken);
            if (stockValue is not string storedStock ||
                decimal.Parse(storedStock, CultureInfo.InvariantCulture) < requiredStock)
                throw new InvalidOperationException($"Estoque insuficiente para preparar '{menuName}'.");
            await using var updateStock = connection.CreateCommand();
            updateStock.Transaction = (SqliteTransaction)transaction;
            updateStock.CommandText = "UPDATE Products SET Stock = $stock WHERE Id = $id;";
            updateStock.Parameters.AddWithValue(
                "$stock",
                (decimal.Parse(storedStock, CultureInfo.InvariantCulture) - requiredStock).ToString(CultureInfo.InvariantCulture));
            updateStock.Parameters.AddWithValue("$id", inventoryProductId);
            await updateStock.ExecuteNonQueryAsync(cancellationToken);
            await SqliteSyncOutboxRepository.EnqueueAsync(
                connection,
                (SqliteTransaction)transaction,
                "restaurant",
                "product-stock",
                inventoryProductId,
                new
                {
                    productId = inventoryProductId,
                    stock = decimal.Parse(storedStock, CultureInfo.InvariantCulture) - requiredStock
                },
                cancellationToken);
        }

        var orderItemId = Guid.NewGuid();
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = """
                INSERT INTO RestaurantOrderItems
                    (Id, OrderId, MenuItemId, MenuItemName, Quantity, UnitPrice, InventoryProductId, InventoryQuantityPerUnit)
                VALUES ($id, $orderId, $menuItemId, $name, $quantity, $unitPrice, $inventoryProductId, $inventoryQuantity);
                """;
            insert.Parameters.AddWithValue("$id", orderItemId.ToString());
            insert.Parameters.AddWithValue("$orderId", orderId);
            insert.Parameters.AddWithValue("$menuItemId", menuItemId.ToString());
            insert.Parameters.AddWithValue("$name", menuName);
            insert.Parameters.AddWithValue("$quantity", quantity.ToString(CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$unitPrice", unitPrice.ToString(CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$inventoryProductId", inventoryProductId ?? (object)DBNull.Value);
            insert.Parameters.AddWithValue("$inventoryQuantity", inventoryQuantityPerUnit.ToString(CultureInfo.InvariantCulture));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await SqliteSyncOutboxRepository.EnqueueAsync(
            connection,
            (SqliteTransaction)transaction,
            "restaurant",
            "order-item",
            orderItemId.ToString(),
            new { orderId, tableId, menuItemId, menuName, quantity, unitPrice },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RemoveOrderItemAsync(
        Guid tableId,
        Guid orderItemId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        string orderId;
        string itemName;
        decimal quantity;
        string? inventoryProductId;
        decimal inventoryQuantityPerUnit;
        await using (var find = connection.CreateCommand())
        {
            find.Transaction = (SqliteTransaction)transaction;
            find.CommandText = """
                SELECT o.Id, i.MenuItemName, i.Quantity,
                       COALESCE(i.InventoryProductId, m.InventoryProductId),
                       CASE WHEN i.InventoryProductId IS NOT NULL
                            THEN i.InventoryQuantityPerUnit
                            ELSE COALESCE(m.InventoryQuantityPerUnit, '0')
                       END
                FROM RestaurantOrders o
                JOIN RestaurantOrderItems i ON i.OrderId = o.Id
                LEFT JOIN RestaurantMenuItems m ON m.Id = i.MenuItemId
                WHERE o.TableId = $tableId AND o.Status = 'Open' AND i.Id = $itemId;
                """;
            find.Parameters.AddWithValue("$tableId", tableId.ToString());
            find.Parameters.AddWithValue("$itemId", orderItemId.ToString());
            await using var reader = await find.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("O item selecionado não pertence a uma comanda aberta desta mesa.");
            orderId = reader.GetString(0);
            itemName = reader.GetString(1);
            quantity = decimal.Parse(reader.GetString(2), CultureInfo.InvariantCulture);
            inventoryProductId = reader.IsDBNull(3) ? null : reader.GetString(3);
            inventoryQuantityPerUnit = decimal.Parse(reader.GetString(4), CultureInfo.InvariantCulture);
        }

        await using (var paid = connection.CreateCommand())
        {
            paid.Transaction = (SqliteTransaction)transaction;
            paid.CommandText = "SELECT COALESCE(SUM(CAST(Amount AS REAL)), 0) FROM RestaurantPayments WHERE OrderId = $orderId;";
            paid.Parameters.AddWithValue("$orderId", orderId);
            if (Convert.ToDecimal(await paid.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) > 0)
                throw new InvalidOperationException("Não é possível remover itens depois de registrar um pagamento parcial.");
        }

        if (inventoryProductId is not null && inventoryQuantityPerUnit > 0)
        {
            var restoredQuantity = quantity * inventoryQuantityPerUnit;
            await using var stock = connection.CreateCommand();
            stock.Transaction = (SqliteTransaction)transaction;
            stock.CommandText = "SELECT Stock FROM Products WHERE Id = $id;";
            stock.Parameters.AddWithValue("$id", inventoryProductId);
            var storedStock = await stock.ExecuteScalarAsync(cancellationToken) as string
                              ?? throw new InvalidOperationException("O produto vinculado ao estoque não existe mais.");
            var newStock = decimal.Parse(storedStock, CultureInfo.InvariantCulture) + restoredQuantity;

            await using var updateStock = connection.CreateCommand();
            updateStock.Transaction = (SqliteTransaction)transaction;
            updateStock.CommandText = "UPDATE Products SET Stock = $stock WHERE Id = $id;";
            updateStock.Parameters.AddWithValue("$stock", newStock.ToString(CultureInfo.InvariantCulture));
            updateStock.Parameters.AddWithValue("$id", inventoryProductId);
            await updateStock.ExecuteNonQueryAsync(cancellationToken);
            await SqliteSyncOutboxRepository.EnqueueAsync(
                connection,
                (SqliteTransaction)transaction,
                "restaurant",
                "product-stock",
                inventoryProductId,
                new { productId = inventoryProductId, stock = newStock },
                cancellationToken);
        }

        await using (var remove = connection.CreateCommand())
        {
            remove.Transaction = (SqliteTransaction)transaction;
            remove.CommandText = "DELETE FROM RestaurantOrderItems WHERE Id = $itemId;";
            remove.Parameters.AddWithValue("$itemId", orderItemId.ToString());
            if (await remove.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("O item da comanda já foi removido.");
        }

        await SqliteSyncOutboxRepository.EnqueueAsync(
            connection,
            (SqliteTransaction)transaction,
            "restaurant",
            "order-item-removed",
            orderItemId.ToString(),
            new { orderId, tableId, orderItemId, itemName, quantity },
            cancellationToken);

        await using (var closeEmptyOrder = connection.CreateCommand())
        {
            closeEmptyOrder.Transaction = (SqliteTransaction)transaction;
            closeEmptyOrder.CommandText = """
                UPDATE RestaurantOrders
                SET Status = 'Cancelled', ClosedAt = $closedAt
                WHERE Id = $orderId
                  AND NOT EXISTS (SELECT 1 FROM RestaurantOrderItems WHERE OrderId = $orderId);
                """;
            closeEmptyOrder.Parameters.AddWithValue(
                "$closedAt",
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            closeEmptyOrder.Parameters.AddWithValue("$orderId", orderId);
            await closeEmptyOrder.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SetOrderEmployeeAsync(Guid tableId, Guid? employeeId, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE RestaurantOrders SET EmployeeId = $employeeId
            WHERE TableId = $tableId AND Status = 'Open'
              AND NOT EXISTS (SELECT 1 FROM RestaurantOrderItems i WHERE i.OrderId = RestaurantOrders.Id);
            """;
        command.Parameters.AddWithValue("$tableId", tableId.ToString());
        command.Parameters.AddWithValue("$employeeId", employeeId?.ToString() ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<RestaurantPaymentRecord> PayOrderAsync(
        Guid tableId,
        PaymentMethod paymentMethod,
        CancellationToken cancellationToken = default)
    {
        var order = await GetOpenOrderAsync(tableId, cancellationToken)
            ?? throw new InvalidOperationException("Não há comanda aberta para esta mesa.");
        return await PayOrderAsync(
            tableId,
            paymentMethod,
            order.RemainingTotal,
            order.WaiterServiceChargeEnabled,
            cancellationToken);
    }

    public async Task<RestaurantPaymentRecord> PayOrderAsync(
        Guid tableId,
        PaymentMethod paymentMethod,
        decimal amount,
        CancellationToken cancellationToken = default)
    {
        var order = await GetOpenOrderAsync(tableId, cancellationToken)
            ?? throw new InvalidOperationException("Não há comanda aberta para esta mesa.");
        return await PayOrderAsync(
            tableId,
            paymentMethod,
            amount,
            order.WaiterServiceChargeEnabled,
            cancellationToken);
    }

    public async Task<RestaurantPaymentRecord> PayOrderAsync(
        Guid tableId,
        PaymentMethod paymentMethod,
        decimal amount,
        bool waiterServiceChargeEnabled,
        CancellationToken cancellationToken = default)
    {
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "O pagamento deve ser maior que zero.");
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var cashSessionCommand = connection.CreateCommand();
        cashSessionCommand.Transaction = (SqliteTransaction)transaction;
        cashSessionCommand.CommandText = "SELECT Id FROM RestaurantCashSessions WHERE ClosedAt IS NULL ORDER BY OpenedAt DESC LIMIT 1;";
        var sessionIdValue = await cashSessionCommand.ExecuteScalarAsync(cancellationToken);
        if (sessionIdValue is not string sessionIdText)
            throw new InvalidOperationException("Abra o caixa antes de receber o pagamento da comanda.");
        var sessionId = Guid.Parse(sessionIdText);
        await using var order = connection.CreateCommand();
        order.Transaction = (SqliteTransaction)transaction;
        order.CommandText = """
            SELECT o.Id, COALESCE(o.WaiterServiceChargeEnabled, 0)
            FROM RestaurantOrders o
            WHERE o.TableId = $tableId AND o.Status = 'Open'
            LIMIT 1;
            """;
        order.Parameters.AddWithValue("$tableId", tableId.ToString());
        await using var reader = await order.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Não há comanda aberta para esta mesa.");
        var orderId = Guid.Parse(reader.GetString(0));
        var currentServiceChargeEnabled = reader.GetInt64(1) != 0;
        await reader.DisposeAsync();
        await using var paidCommand = connection.CreateCommand();
        paidCommand.Transaction = (SqliteTransaction)transaction;
        paidCommand.CommandText = "SELECT COALESCE(SUM(CAST(Amount AS REAL)), 0) FROM RestaurantPayments WHERE OrderId = $orderId;";
        paidCommand.Parameters.AddWithValue("$orderId", orderId.ToString());
        var paidTotal = Convert.ToDecimal(await paidCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        if (paidTotal > 0 && currentServiceChargeEnabled != waiterServiceChargeEnabled)
            throw new InvalidOperationException("A taxa do garçom não pode ser alterada depois do primeiro pagamento.");
        if (paidTotal == 0 && currentServiceChargeEnabled != waiterServiceChargeEnabled)
        {
            await using var updateServiceCharge = connection.CreateCommand();
            updateServiceCharge.Transaction = (SqliteTransaction)transaction;
            updateServiceCharge.CommandText = """
                UPDATE RestaurantOrders SET WaiterServiceChargeEnabled = $enabled
                WHERE Id = $orderId AND Status = 'Open';
                """;
            updateServiceCharge.Parameters.AddWithValue("$enabled", waiterServiceChargeEnabled ? 1 : 0);
            updateServiceCharge.Parameters.AddWithValue("$orderId", orderId.ToString());
            await updateServiceCharge.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var itemTotals = connection.CreateCommand();
        itemTotals.Transaction = (SqliteTransaction)transaction;
        itemTotals.CommandText = "SELECT Quantity, UnitPrice FROM RestaurantOrderItems WHERE OrderId = $orderId;";
        itemTotals.Parameters.AddWithValue("$orderId", orderId.ToString());
        decimal total = 0;
        await using (var itemsReader = await itemTotals.ExecuteReaderAsync(cancellationToken))
        {
            while (await itemsReader.ReadAsync(cancellationToken))
            {
                total += decimal.Parse(itemsReader.GetString(0), CultureInfo.InvariantCulture) *
                         decimal.Parse(itemsReader.GetString(1), CultureInfo.InvariantCulture);
            }
        }
        total = decimal.Round(total, 2, MidpointRounding.AwayFromZero);
        var subtotal = total;
        var waiterServiceCharge = waiterServiceChargeEnabled
            ? decimal.Round(subtotal * 0.10m, 2, MidpointRounding.AwayFromZero)
            : 0m;
        total += waiterServiceCharge;
        if (total <= 0)
            throw new InvalidOperationException("Adicione itens à comanda antes de receber o pagamento.");
        var remaining = decimal.Round(total - paidTotal, 2, MidpointRounding.AwayFromZero);
        if (remaining <= 0)
            throw new InvalidOperationException("A comanda já está totalmente paga.");
        if (amount > remaining)
            throw new InvalidOperationException($"O valor informado excede o saldo de {remaining:C2}.");

        var paidAt = DateTimeOffset.UtcNow;
        await using var payment = connection.CreateCommand();
        payment.Transaction = (SqliteTransaction)transaction;
        payment.CommandText = """
            INSERT INTO RestaurantPayments (Id, OrderId, SessionId, PaidAt, PaymentMethod, Amount)
            VALUES ($id, $orderId, $sessionId, $paidAt, $method, $amount);
            """;
        var paymentId = Guid.NewGuid();
        payment.Parameters.AddWithValue("$id", paymentId.ToString());
        payment.Parameters.AddWithValue("$orderId", orderId.ToString());
        payment.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        payment.Parameters.AddWithValue("$paidAt", paidAt.ToString("O", CultureInfo.InvariantCulture));
        payment.Parameters.AddWithValue("$method", paymentMethod.ToString());
        payment.Parameters.AddWithValue("$amount", amount.ToString(CultureInfo.InvariantCulture));
        await payment.ExecuteNonQueryAsync(cancellationToken);
        var newRemaining = decimal.Round(remaining - amount, 2, MidpointRounding.AwayFromZero);
        if (newRemaining == 0)
        {
            await using var close = connection.CreateCommand();
            close.Transaction = (SqliteTransaction)transaction;
            close.CommandText = "UPDATE RestaurantOrders SET Status = 'Paid', ClosedAt = $paidAt WHERE Id = $orderId;";
            close.Parameters.AddWithValue("$paidAt", paidAt.ToString("O", CultureInfo.InvariantCulture));
            close.Parameters.AddWithValue("$orderId", orderId.ToString());
            await close.ExecuteNonQueryAsync(cancellationToken);
        }
        await SqliteSyncOutboxRepository.EnqueueAsync(
            connection,
            (SqliteTransaction)transaction,
            "restaurant",
            "payment",
            paymentId.ToString(),
            new
            {
                paymentId,
                orderId,
                sessionId,
                paidAt = paidAt.ToString("O", CultureInfo.InvariantCulture),
                paymentMethod = paymentMethod.ToString(),
                amount,
                subtotal,
                waiterServiceCharge,
                total,
                waiterServiceChargeEnabled,
                paidTotal = paidTotal + amount,
                remainingTotal = newRemaining
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new RestaurantPaymentRecord(orderId, paymentMethod, amount, paidAt, newRemaining);
    }

    public async Task<RestaurantCashSessionRecord?> GetOpenCashSessionAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.Id, s.OpenedAt, s.OpeningAmount,
              COALESCE((SELECT SUM(CAST(p.Amount AS REAL)) FROM RestaurantPayments p
                WHERE p.SessionId = s.Id AND p.PaymentMethod = 'Cash'), 0),
              COALESCE((SELECT SUM(CAST(p.Amount AS REAL)) FROM RestaurantPayments p
                WHERE p.SessionId = s.Id AND p.PaymentMethod <> 'Cash'), 0)
            FROM RestaurantCashSessions s WHERE s.ClosedAt IS NULL ORDER BY s.OpenedAt DESC LIMIT 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new RestaurantCashSessionRecord(
                Guid.Parse(reader.GetString(0)),
                DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                decimal.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                Convert.ToDecimal(reader.GetDouble(3), CultureInfo.InvariantCulture),
                Convert.ToDecimal(reader.GetDouble(4), CultureInfo.InvariantCulture))
            : null;
    }

    public async Task OpenCashSessionAsync(decimal openingAmount, CancellationToken cancellationToken = default)
    {
        if (openingAmount < 0)
            throw new ArgumentOutOfRangeException(nameof(openingAmount), "O valor de abertura não pode ser negativo.");
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var id = Guid.NewGuid();
        var openedAt = DateTimeOffset.UtcNow;
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO RestaurantCashSessions (Id, OpenedAt, OpeningAmount)
            SELECT $id, $openedAt, $opening
            WHERE NOT EXISTS (SELECT 1 FROM RestaurantCashSessions WHERE ClosedAt IS NULL);
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$openedAt", openedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$opening", openingAmount.ToString(CultureInfo.InvariantCulture));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Já existe um caixa aberto.");
        await SqliteSyncOutboxRepository.EnqueueAsync(
            connection,
            (SqliteTransaction)transaction,
            "restaurant",
            "cash-session-open",
            id.ToString(),
            new { id, openedAt = openedAt.ToString("O", CultureInfo.InvariantCulture), openingAmount },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task CloseCashSessionAsync(Guid sessionId, decimal closingAmount, CancellationToken cancellationToken = default)
    {
        if (closingAmount < 0)
            throw new ArgumentOutOfRangeException(nameof(closingAmount), "O valor contado não pode ser negativo.");
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var openOrders = connection.CreateCommand();
        openOrders.Transaction = (SqliteTransaction)transaction;
        openOrders.CommandText = "SELECT COUNT(*) FROM RestaurantOrders WHERE Status = 'Open';";
        if (Convert.ToInt32(await openOrders.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) > 0)
            throw new InvalidOperationException("Feche ou transfira as comandas abertas antes de encerrar o caixa.");
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            UPDATE RestaurantCashSessions SET ClosedAt = $closedAt, ClosingAmount = $closing
            WHERE Id = $id AND ClosedAt IS NULL;
            """;
        var closedAt = DateTimeOffset.UtcNow;
        command.Parameters.AddWithValue("$closedAt", closedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$closing", closingAmount.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$id", sessionId.ToString());
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("O caixa já foi encerrado ou não existe.");
        await SqliteSyncOutboxRepository.EnqueueAsync(
            connection,
            (SqliteTransaction)transaction,
            "restaurant",
            "cash-session-close",
            sessionId.ToString(),
            new { sessionId, closedAt = closedAt.ToString("O", CultureInfo.InvariantCulture), closingAmount },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static string ValidateName(string value, string field)
    {
        var name = value.Trim();
        if (name.Length is < 2 or > 100)
            throw new ArgumentException($"O nome de {field} deve ter entre 2 e 100 caracteres.");
        return name;
    }
}
