using System.Globalization;
using System.Security.Cryptography;
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
            SELECT a.Id, a.Name, a.SortOrder, COALESCE(SUM(CASE WHEN t.IsActive = 1 THEN 1 ELSE 0 END), 0), a.IsActive
            FROM RestaurantAreas a LEFT JOIN RestaurantTables t ON t.AreaId = a.Id
            GROUP BY a.Id, a.IsActive ORDER BY a.SortOrder, a.Name;
            """;
        var result = new List<RestaurantAreaRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new RestaurantAreaRecord(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetInt64(4) != 0));
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
            VALUES ($id, $name, COALESCE((SELECT MAX(SortOrder) + 1 FROM RestaurantAreas), 0))
            RETURNING SortOrder;
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$name", name);
        try
        {
            var sortOrder = Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);
            await SqliteSyncOutboxRepository.EnqueueAsync(
                connection,
                (SqliteTransaction)transaction,
                "restaurant",
                "area",
                id.ToString(),
                new { id, name, sortOrder, isActive = true },
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return id;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException($"A área '{name}' já existe.", exception);
        }
    }

    public async Task SetAreaActiveAsync(
        Guid areaId,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using var verify = connection.CreateCommand();
        verify.Transaction = (SqliteTransaction)transaction;
        verify.CommandText = """
            SELECT IsActive,
                   EXISTS (SELECT 1 FROM RestaurantOrders o
                           JOIN RestaurantTables t ON t.Id = o.TableId
                           WHERE t.AreaId = RestaurantAreas.Id AND o.Status = 'Open'),
                   (SELECT COUNT(*) FROM RestaurantAreas WHERE IsActive = 1)
            FROM RestaurantAreas WHERE Id = $areaId;
            """;
        verify.Parameters.AddWithValue("$areaId", areaId.ToString());
        await using var reader = await verify.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("A área selecionada não existe.");
        var wasActive = reader.GetInt64(0) != 0;
        var hasOpenOrders = reader.GetInt64(1) != 0;
        var activeAreaCount = reader.GetInt64(2);
        await reader.DisposeAsync();

        if (wasActive == isActive)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }
        if (!isActive && hasOpenOrders)
            throw new InvalidOperationException("Feche as comandas abertas da área antes de desativá-la.");
        if (!isActive && activeAreaCount <= 1)
            throw new InvalidOperationException("Mantenha pelo menos uma área ativa no restaurante.");

        await using var update = connection.CreateCommand();
        update.Transaction = (SqliteTransaction)transaction;
        update.CommandText = "UPDATE RestaurantAreas SET IsActive = $isActive WHERE Id = $areaId;";
        update.Parameters.AddWithValue("$isActive", isActive ? 1 : 0);
        update.Parameters.AddWithValue("$areaId", areaId.ToString());
        await update.ExecuteNonQueryAsync(cancellationToken);

        await SqliteSyncOutboxRepository.EnqueueAsync(
            connection,
            (SqliteTransaction)transaction,
            "restaurant",
            "area",
            areaId.ToString(),
            new { areaId, isActive },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
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
        verify.CommandText = "SELECT IsActive FROM RestaurantAreas WHERE Id = $areaId;";
        verify.Parameters.AddWithValue("$areaId", areaId.ToString());
        var areaIsActive = await verify.ExecuteScalarAsync(cancellationToken);
        if (areaIsActive is null || areaIsActive == DBNull.Value)
            throw new InvalidOperationException("A área selecionada não existe.");
        if (Convert.ToInt32(areaIsActive, CultureInfo.InvariantCulture) != 1)
            throw new InvalidOperationException("Não é possível alterar as mesas de uma área desativada.");

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

    public async Task<IReadOnlyList<RestaurantTableRecord>> GetTablesAsync(
        Guid areaId,
        CancellationToken cancellationToken = default,
        bool includeInactive = false)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.Id, t.AreaId, t.Number, COALESCE(o.Id, ''), ROUND(COALESCE((
                SELECT SUM(CAST(i.Quantity AS REAL) * CAST(i.UnitPrice AS REAL))
                FROM RestaurantOrderItems i WHERE i.OrderId = o.Id), 0) *
                (1 + CASE WHEN COALESCE(o.WaiterServiceChargeEnabled, 0) = 1 THEN 0.10 ELSE 0 END), 2),
                t.IsActive, COALESCE(o.CustomerName, ''), COALESCE(e.Name, '')
            FROM RestaurantTables t
            LEFT JOIN RestaurantOrders o ON o.TableId = t.Id AND o.Status = 'Open'
            LEFT JOIN RestaurantEmployees e ON e.Id = o.EmployeeId
            WHERE t.AreaId = $areaId
              AND ($includeInactive = 1 OR (
                  t.IsActive = 1
                  AND EXISTS (SELECT 1 FROM RestaurantAreas a WHERE a.Id = t.AreaId AND a.IsActive = 1)
              ))
            ORDER BY t.Number;
            """;
        command.Parameters.AddWithValue("$areaId", areaId.ToString());
        command.Parameters.AddWithValue("$includeInactive", includeInactive ? 1 : 0);
        var result = new List<RestaurantTableRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new RestaurantTableRecord(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetInt32(2),
                reader.GetString(3).Length > 0,
                Convert.ToDecimal(reader.GetDouble(4), CultureInfo.InvariantCulture),
                reader.GetInt64(5) != 0, reader.GetString(6), reader.GetString(7)));
        return result;
    }

    public async Task<IReadOnlyList<RestaurantMenuItemRecord>> GetMenuItemsAsync(bool activeOnly = false, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Name, Category, Price, IsActive, InventoryProductId, InventoryQuantityPerUnit, PrintTarget, PriceM, PriceG, Description
            FROM RestaurantMenuItems
            WHERE $activeOnly = 0 OR IsActive = 1 ORDER BY IsActive DESC, Category, Name;
            """;
        command.Parameters.AddWithValue("$activeOnly", activeOnly ? 1 : 0);
        var result = new List<RestaurantMenuItemRecord>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                result.Add(new RestaurantMenuItemRecord(
                    Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
                    decimal.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                    reader.GetInt64(4) != 0,
                    reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5)),
                    decimal.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
                    PrintTarget: reader.IsDBNull(7) ? "kitchen" : reader.GetString(7),
                    PriceM: reader.IsDBNull(8) ? null : decimal.Parse(reader.GetString(8), CultureInfo.InvariantCulture),
                    PriceG: reader.IsDBNull(9) ? null : decimal.Parse(reader.GetString(9), CultureInfo.InvariantCulture),
                    Description: reader.GetString(10)));
        }

        await using var optionsCommand = connection.CreateCommand();
        optionsCommand.CommandText = """
            SELECT Id, MenuItemId, Name, ExtraPrice FROM RestaurantMenuItemOptions
            ORDER BY MenuItemId, SortOrder, rowid;
            """;
        var optionsByItem = new Dictionary<Guid, List<RestaurantMenuOptionRecord>>();
        await using (var optionsReader = await optionsCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await optionsReader.ReadAsync(cancellationToken))
            {
                var itemId = Guid.Parse(optionsReader.GetString(1));
                if (!optionsByItem.TryGetValue(itemId, out var list))
                    optionsByItem[itemId] = list = [];
                list.Add(new RestaurantMenuOptionRecord(
                    Guid.Parse(optionsReader.GetString(0)),
                    optionsReader.GetString(2),
                    decimal.Parse(optionsReader.GetString(3), CultureInfo.InvariantCulture)));
            }
        }
        return result
            .Select(item => item with { Options = optionsByItem.GetValueOrDefault(item.Id) ?? [] })
            .ToList();
    }

    public async Task SaveMenuItemAsync(RestaurantMenuItemRecord item, CancellationToken cancellationToken = default)
    {
        var name = ValidateName(item.Name, "item");
        var description = (item.Description ?? string.Empty).Trim();
        if (description.Length > 500)
            throw new ArgumentOutOfRangeException(nameof(item), "A descrição pode conter no máximo 500 caracteres.");
        if (item.Price <= 0)
            throw new ArgumentOutOfRangeException(nameof(item), "O preço do item deve ser maior que zero.");
        if (item.PriceM is <= 0 || item.PriceG is <= 0)
            throw new ArgumentOutOfRangeException(nameof(item), "Os preços dos tamanhos devem ser maiores que zero.");
        if (item.PrintTarget is not ("kitchen" or "bar"))
            throw new ArgumentException("Destino de impressão inválido.", nameof(item));
        if (item.InventoryQuantityPerUnit < 0 ||
            (item.InventoryProductId is null && item.InventoryQuantityPerUnit != 0) ||
            (item.InventoryProductId is not null && item.InventoryQuantityPerUnit <= 0))
            throw new ArgumentOutOfRangeException(nameof(item), "Informe um produto e uma quantidade de estoque válida por unidade.");
        var options = NormalizeOptions(item.Options);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO RestaurantMenuItems
                (Id, Name, Category, Price, IsActive, InventoryProductId, InventoryQuantityPerUnit, PrintTarget, PriceM, PriceG, Description)
            VALUES ($id, $name, $category, $price, $active, $productId, $quantity, $printTarget, $priceM, $priceG, $description)
            ON CONFLICT(Id) DO UPDATE SET Name=excluded.Name, Category=excluded.Category,
                Price=excluded.Price, IsActive=excluded.IsActive,
                InventoryProductId=excluded.InventoryProductId,
                InventoryQuantityPerUnit=excluded.InventoryQuantityPerUnit,
                PrintTarget=excluded.PrintTarget, PriceM=excluded.PriceM, PriceG=excluded.PriceG,
                Description=excluded.Description;
            """;
        command.Parameters.AddWithValue("$id", item.Id.ToString());
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$category", item.Category.Trim());
        command.Parameters.AddWithValue("$price", item.Price.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$active", item.IsActive ? 1 : 0);
        command.Parameters.AddWithValue("$productId", item.InventoryProductId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$quantity", item.InventoryQuantityPerUnit.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$printTarget", item.PrintTarget ?? "kitchen");
        command.Parameters.AddWithValue("$priceM", item.PriceM?.ToString(CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$priceG", item.PriceG?.ToString(CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$description", description);
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
            if (options is not null)
            {
                await using var clear = connection.CreateCommand();
                clear.Transaction = (SqliteTransaction)transaction;
                clear.CommandText = "DELETE FROM RestaurantMenuItemOptions WHERE MenuItemId = $id;";
                clear.Parameters.AddWithValue("$id", item.Id.ToString());
                await clear.ExecuteNonQueryAsync(cancellationToken);
                var order = 0;
                foreach (var option in options)
                {
                    await using var insertOption = connection.CreateCommand();
                    insertOption.Transaction = (SqliteTransaction)transaction;
                    insertOption.CommandText = """
                        INSERT INTO RestaurantMenuItemOptions (Id, MenuItemId, Name, ExtraPrice, SortOrder)
                        VALUES ($id, $menuItemId, $name, $extra, $order);
                        """;
                    insertOption.Parameters.AddWithValue("$id", option.Id.ToString());
                    insertOption.Parameters.AddWithValue("$menuItemId", item.Id.ToString());
                    insertOption.Parameters.AddWithValue("$name", option.Name);
                    insertOption.Parameters.AddWithValue("$extra", option.ExtraPrice.ToString(CultureInfo.InvariantCulture));
                    insertOption.Parameters.AddWithValue("$order", order++);
                    await insertOption.ExecuteNonQueryAsync(cancellationToken);
                }
            }
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
                    item.InventoryQuantityPerUnit,
                    description,
                    Options = options
                },
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException($"O item '{name}' já existe no cardápio.", exception);
        }
    }

    public async Task ReplaceWithPesqueiroMenuAsync(CancellationToken cancellationToken = default)
    {
        var items = PesqueiroMenu.Create();
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = "SELECT COUNT(*) FROM RestaurantOrders WHERE Status = 'Open';";
            if (Convert.ToInt64(await check.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 0)
                throw new InvalidOperationException("Feche todas as comandas antes de substituir o cardápio.");
        }
        await using (var deactivate = connection.CreateCommand())
        {
            deactivate.Transaction = transaction;
            deactivate.CommandText = "UPDATE RestaurantMenuItems SET IsActive = 0;";
            await deactivate.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var item in items)
        {
            string itemId;
            await using (var upsert = connection.CreateCommand())
            {
                upsert.Transaction = transaction;
                upsert.CommandText = """
                    INSERT INTO RestaurantMenuItems
                      (Id, Name, Category, Price, IsActive, InventoryProductId, InventoryQuantityPerUnit, PrintTarget, PriceM, PriceG, Description)
                    VALUES ($id, $name, $category, $price, 1, NULL, '0', $target, $m, $g, $description)
                    ON CONFLICT(Name) DO UPDATE SET Category=excluded.Category, Price=excluded.Price,
                      IsActive=1, InventoryProductId=NULL, InventoryQuantityPerUnit='0',
                      PrintTarget=excluded.PrintTarget, PriceM=excluded.PriceM, PriceG=excluded.PriceG, Description=excluded.Description
                    RETURNING Id;
                    """;
                upsert.Parameters.AddWithValue("$id", item.Id.ToString());
                upsert.Parameters.AddWithValue("$name", item.Name);
                upsert.Parameters.AddWithValue("$category", item.Category);
                upsert.Parameters.AddWithValue("$price", item.Price.ToString(CultureInfo.InvariantCulture));
                upsert.Parameters.AddWithValue("$target", item.PrintTarget);
                upsert.Parameters.AddWithValue("$m", item.PriceM?.ToString(CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
                upsert.Parameters.AddWithValue("$g", item.PriceG?.ToString(CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
                upsert.Parameters.AddWithValue("$description", item.Description);
                itemId = (string)(await upsert.ExecuteScalarAsync(cancellationToken)
                    ?? throw new InvalidOperationException("Não foi possível salvar um item do cardápio."));
            }
            await using (var clear = connection.CreateCommand())
            {
                clear.Transaction = transaction;
                clear.CommandText = "DELETE FROM RestaurantMenuItemOptions WHERE MenuItemId = $id;";
                clear.Parameters.AddWithValue("$id", itemId);
                await clear.ExecuteNonQueryAsync(cancellationToken);
            }
            var sortOrder = 0;
            foreach (var option in item.Options ?? [])
            {
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO RestaurantMenuItemOptions (Id, MenuItemId, Name, ExtraPrice, SortOrder)
                    VALUES ($id, $itemId, $name, $price, $sortOrder);
                    """;
                insert.Parameters.AddWithValue("$id", option.Id.ToString());
                insert.Parameters.AddWithValue("$itemId", itemId);
                insert.Parameters.AddWithValue("$name", option.Name);
                insert.Parameters.AddWithValue("$price", option.ExtraPrice.ToString(CultureInfo.InvariantCulture));
                insert.Parameters.AddWithValue("$sortOrder", sortOrder++);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        await SqliteSyncOutboxRepository.EnqueueAsync(connection, transaction, "restaurant", "menu-replaced",
            Guid.NewGuid().ToString(), new { source = "pesqueiro", count = items.Count }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<int> UpdatePesqueiroDescriptionsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var updated = 0;
        foreach (var item in PesqueiroMenu.Create().Where(item => !string.IsNullOrEmpty(item.Description)))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE RestaurantMenuItems SET Description = $description
                WHERE Name = $name COLLATE NOCASE AND Category = $category COLLATE NOCASE
                  AND IsActive = 1 AND TRIM(Description) = '';
                """;
            command.Parameters.AddWithValue("$description", item.Description);
            command.Parameters.AddWithValue("$name", item.Name);
            command.Parameters.AddWithValue("$category", item.Category);
            updated += await command.ExecuteNonQueryAsync(cancellationToken);
        }
        if (updated > 0)
            await SqliteSyncOutboxRepository.EnqueueAsync(connection, transaction, "restaurant", "menu-replaced",
                Guid.NewGuid().ToString(), new { source = "pesqueiro-descriptions", count = updated }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    public async Task<IReadOnlyList<RestaurantEmployeeRecord>> GetEmployeesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name, Role, IsActive, WaiterNumber, FirebaseUid, AreaIds, LocalPinHash FROM RestaurantEmployees ORDER BY IsActive DESC, Name;";
        var result = new List<RestaurantEmployeeRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new RestaurantEmployeeRecord(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3) != 0,
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6),
                !reader.IsDBNull(7)));
        return result;
    }

    private const int LocalPinIterations = 210_000;

    public static string CreateLocalWaiterUid(Guid employeeId) => $"local:{employeeId}";

    public async Task SetEmployeeLocalPinAsync(
        Guid employeeId,
        string pin,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pin) || pin.Length < 4 || pin.Length > 12 || !pin.All(char.IsAsciiDigit))
            throw new InvalidOperationException("O PIN local deve ter de 4 a 12 dígitos numéricos.");

        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(pin, salt, LocalPinIterations, HashAlgorithmName.SHA256, 32);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE RestaurantEmployees
            SET LocalPinSalt = $salt, LocalPinHash = $hash
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", employeeId.ToString());
        command.Parameters.AddWithValue("$salt", Convert.ToBase64String(salt));
        command.Parameters.AddWithValue("$hash", Convert.ToBase64String(hash));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("O funcionário informado não existe.");
    }

    public async Task ClearEmployeeLocalPinAsync(Guid employeeId, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE RestaurantEmployees
            SET LocalPinSalt = NULL, LocalPinHash = NULL
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", employeeId.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<RestaurantEmployeeRecord?> VerifyWaiterLocalPinAsync(
        string waiterNumber,
        string pin,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(waiterNumber) || string.IsNullOrWhiteSpace(pin))
            return null;

        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Name, Role, IsActive, WaiterNumber, FirebaseUid, AreaIds, LocalPinSalt, LocalPinHash
            FROM RestaurantEmployees
            WHERE WaiterNumber = $waiterNumber COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$waiterNumber", waiterNumber.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        if (reader.GetInt64(3) == 0 || reader.IsDBNull(7) || reader.IsDBNull(8))
            return null;

        try
        {
            var salt = Convert.FromBase64String(reader.GetString(7));
            var expected = Convert.FromBase64String(reader.GetString(8));
            var actual = Rfc2898DeriveBytes.Pbkdf2(pin, salt, LocalPinIterations, HashAlgorithmName.SHA256, expected.Length);
            if (!CryptographicOperations.FixedTimeEquals(actual, expected))
                return null;
        }
        catch (FormatException)
        {
            return null;
        }

        return new RestaurantEmployeeRecord(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            true,
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetString(6),
            HasLocalPin: true);
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
            INSERT INTO RestaurantEmployees (Id, Name, Role, IsActive, WaiterNumber, FirebaseUid, AreaIds)
            VALUES ($id, $name, $role, $active, $waiterNumber, $firebaseUid, $areaIds)
            ON CONFLICT(Id) DO UPDATE SET Name=excluded.Name, Role=excluded.Role, IsActive=excluded.IsActive,
                WaiterNumber=excluded.WaiterNumber, FirebaseUid=excluded.FirebaseUid, AreaIds=excluded.AreaIds;
            """;
        command.Parameters.AddWithValue("$id", employee.Id.ToString());
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$role", string.IsNullOrWhiteSpace(employee.Role) ? "Atendente" : employee.Role.Trim());
        command.Parameters.AddWithValue("$active", employee.IsActive ? 1 : 0);
        command.Parameters.AddWithValue("$waiterNumber", (object?)employee.WaiterNumber ?? DBNull.Value);
        command.Parameters.AddWithValue("$firebaseUid", (object?)employee.FirebaseUid ?? DBNull.Value);
        command.Parameters.AddWithValue("$areaIds", employee.AreaIds ?? string.Empty);
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
            await SqliteSyncOutboxRepository.EnqueueAsync(
                connection,
                (SqliteTransaction)transaction,
                "restaurant",
                "employee",
                employee.Id.ToString(),
                new
                {
                    employee.Id,
                    name,
                    employee.Role,
                    employee.IsActive,
                    employee.WaiterNumber,
                    employee.FirebaseUid,
                    employee.AreaIds
                },
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            var message = exception.Message.Contains("WaiterNumber", StringComparison.OrdinalIgnoreCase)
                ? $"Já existe um funcionário com o número '{employee.WaiterNumber}'."
                : $"O funcionário '{name}' já está cadastrado.";
            throw new InvalidOperationException(message, exception);
        }
    }

    private static async Task<int> GetNextDailyOrderNumberAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        // A numeração recomeça após o último fechamento de caixa.
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COALESCE(MAX(o.DailyNumber), 0) + 1
            FROM RestaurantOrders o
            WHERE o.OpenedAt >= COALESCE(
                (SELECT MAX(s.ClosedAt) FROM RestaurantCashSessions s WHERE s.ClosedAt IS NOT NULL),
                '');
            """;
        return (int)(long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }

    public async Task<Guid> OpenTableAsync(Guid tableId, Guid? employeeId, CancellationToken cancellationToken = default)
    {
        var orderId = Guid.NewGuid();
        var openedAt = DateTimeOffset.UtcNow;
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var dailyNumber = await GetNextDailyOrderNumberAsync(
            connection, (SqliteTransaction)transaction, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO RestaurantOrders (Id, TableId, EmployeeId, OpenedAt, Status, DailyNumber)
            SELECT $id, t.Id, $employeeId, $openedAt, 'Open', $dailyNumber
            FROM RestaurantTables t
            JOIN RestaurantAreas a ON a.Id = t.AreaId
            WHERE t.Id = $tableId AND t.IsActive = 1 AND a.IsActive = 1;
            """;
        command.Parameters.AddWithValue("$id", orderId.ToString());
        command.Parameters.AddWithValue("$tableId", tableId.ToString());
        command.Parameters.AddWithValue("$employeeId", employeeId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$openedAt", openedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$dailyNumber", dailyNumber);
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
                   COALESCE(o.WaiterServiceChargeEnabled, 0), o.DailyNumber, o.CustomerName
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
        var dailyNumber = reader.IsDBNull(5) ? (int?)null : (int)reader.GetInt64(5);
        var customerName = reader.GetString(6);
        await reader.DisposeAsync();

        await using var itemsCommand = connection.CreateCommand();
        itemsCommand.CommandText = """
            SELECT Id, MenuItemName, Quantity, UnitPrice, Observation, MenuItemId, VariantKey FROM RestaurantOrderItems
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
                decimal.Parse(itemsReader.GetString(3), CultureInfo.InvariantCulture),
                itemsReader.IsDBNull(4) ? null : itemsReader.GetString(4),
                MenuItemId: itemsReader.IsDBNull(5) ? null : Guid.Parse(itemsReader.GetString(5)),
                VariantKey: itemsReader.IsDBNull(6) ? null : itemsReader.GetString(6)));
        await itemsReader.DisposeAsync();
        await using var allocations = connection.CreateCommand();
        allocations.CommandText = """
            SELECT a.OrderItemId, a.Quantity FROM RestaurantPaymentItems a
            JOIN RestaurantPayments p ON p.Id = a.PaymentId WHERE p.OrderId = $orderId;
            """;
        allocations.Parameters.AddWithValue("$orderId", id.ToString());
        var paidQuantities = new Dictionary<Guid, decimal>();
        await using (var allocationReader = await allocations.ExecuteReaderAsync(cancellationToken))
        {
            while (await allocationReader.ReadAsync(cancellationToken))
            {
                var itemId = Guid.Parse(allocationReader.GetString(0));
                paidQuantities[itemId] = paidQuantities.GetValueOrDefault(itemId) +
                    decimal.Parse(allocationReader.GetString(1), CultureInfo.InvariantCulture);
            }
        }
        items = items.Select(item => item with { PaidQuantity = paidQuantities.GetValueOrDefault(item.Id) }).ToList();
        await using var paidCommand = connection.CreateCommand();
        paidCommand.CommandText = """
            SELECT Amount, EXISTS(SELECT 1 FROM RestaurantPaymentItems a WHERE a.PaymentId = p.Id)
            FROM RestaurantPayments p WHERE OrderId = $orderId;
            """;
        paidCommand.Parameters.AddWithValue("$orderId", id.ToString());
        decimal paidTotal = 0;
        var hasUnallocatedPayments = false;
        await using (var paymentReader = await paidCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await paymentReader.ReadAsync(cancellationToken))
            {
                paidTotal += decimal.Parse(paymentReader.GetString(0), CultureInfo.InvariantCulture);
                hasUnallocatedPayments |= paymentReader.GetInt64(1) == 0;
            }
        }
        return new RestaurantOrderRecord(id, table, employee, openedAt, items, paidTotal, waiterServiceChargeEnabled, dailyNumber, customerName, hasUnallocatedPayments);
    }

    public Task<IReadOnlyList<KitchenPrintBatchRecord>> GetKitchenPrintBatchesAsync(
        bool failedOnly,
        CancellationToken cancellationToken = default) =>
        GetKitchenPrintBatchesCoreAsync(
            failedOnly ? "i.KitchenPrintStatus = 'failed'" : "i.KitchenPrintStatus IS NULL",
            null,
            cancellationToken);

    public Task<IReadOnlyList<KitchenPrintBatchRecord>> GetUnprintedKitchenBatchesForTableAsync(
        Guid tableId,
        CancellationToken cancellationToken = default) =>
        GetKitchenPrintBatchesCoreAsync(
            "(i.KitchenPrintStatus IS NULL OR i.KitchenPrintStatus = 'failed')",
            tableId,
            cancellationToken);

    public Task<IReadOnlyList<KitchenPrintBatchRecord>> GetAllKitchenBatchesForTableAsync(
        Guid tableId,
        CancellationToken cancellationToken = default) =>
        GetKitchenPrintBatchesCoreAsync("1 = 1", tableId, cancellationToken);

    private async Task<IReadOnlyList<KitchenPrintBatchRecord>> GetKitchenPrintBatchesCoreAsync(
        string statusFilter,
        Guid? tableId,
        CancellationToken cancellationToken)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT o.Id, t.Number, a.Name, COALESCE(sender.Name, e.Name, ''), o.OpenedAt, o.DailyNumber,
                   i.Id, i.MenuItemName, i.Quantity, i.UnitPrice, i.Observation, i.KitchenPrintError,
                   i.PrintBatchNumber, COALESCE(m.PrintTarget, 'kitchen'), i.PrintBatchKey, o.CustomerName
            FROM RestaurantOrderItems i
            JOIN RestaurantOrders o ON o.Id = i.OrderId AND o.Status = 'Open'
            JOIN RestaurantTables t ON t.Id = o.TableId
            JOIN RestaurantAreas a ON a.Id = t.AreaId
            LEFT JOIN RestaurantEmployees e ON e.Id = o.EmployeeId
            LEFT JOIN RestaurantEmployees sender ON sender.Id = i.SendingEmployeeId
            LEFT JOIN RestaurantMenuItems m ON m.Id = i.MenuItemId
            WHERE {statusFilter} {(tableId is null ? "" : "AND o.TableId = $tableId")}
            ORDER BY o.OpenedAt, o.Id, i.rowid;
            """;
        if (tableId is not null)
            command.Parameters.AddWithValue("$tableId", tableId.Value.ToString());
        var groups = new Dictionary<(Guid OrderId, int? Batch, string? Key), (int Table, string Area, string Employee, DateTimeOffset OpenedAt, int? DailyNumber, List<RestaurantOrderItemRecord> Items, string? Error, int? PrintBatchNumber, string CustomerName)>();
        var order = new List<(Guid OrderId, int? Batch, string? Key)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var orderId = Guid.Parse(reader.GetString(0));
            var key = (orderId, reader.IsDBNull(12) ? (int?)null : (int)reader.GetInt64(12),
                reader.IsDBNull(14) ? null : reader.GetString(14));
            if (!groups.TryGetValue(key, out var group))
            {
                group = (
                    (int)reader.GetInt64(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                    reader.IsDBNull(5) ? null : (int)reader.GetInt64(5),
                    [],
                    null,
                    null,
                    reader.GetString(15));
                order.Add(key);
            }
            group.Items.Add(new RestaurantOrderItemRecord(
                Guid.Parse(reader.GetString(6)),
                reader.GetString(7),
                decimal.Parse(reader.GetString(8), CultureInfo.InvariantCulture),
                decimal.Parse(reader.GetString(9), CultureInfo.InvariantCulture),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.GetString(13)));
            if (!reader.IsDBNull(11))
                group.Error = reader.GetString(11);
            if (!reader.IsDBNull(12))
                group.PrintBatchNumber = (int)reader.GetInt64(12);
            groups[key] = group;
        }
        return order
            .Select(key =>
            {
                var group = groups[key];
                return new KitchenPrintBatchRecord(
                    key.OrderId, group.Table, group.Area, group.Employee, group.OpenedAt, group.Items, group.DailyNumber, group.Error, group.PrintBatchNumber, group.CustomerName);
            })
            .ToArray();
    }

    public async Task MarkKitchenPrintAsync(
        IEnumerable<Guid> itemIds,
        bool printed,
        string? error = null,
        CancellationToken cancellationToken = default,
        string? printTarget = null)
    {
        if (printTarget is not null and not ("kitchen" or "bar"))
            throw new ArgumentException("Destino de impressão inválido.", nameof(printTarget));
        var ids = itemIds.ToArray();
        if (ids.Length == 0)
            return;
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var id in ids)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                UPDATE RestaurantOrderItems
                SET KitchenPrintStatus = $status, KitchenPrintError = $error,
                    KitchenPrintTarget = CASE WHEN $status = 'printed' THEN $target ELSE KitchenPrintTarget END
                WHERE Id = $id;
                """;
            command.Parameters.AddWithValue("$status", printed ? "printed" : "failed");
            command.Parameters.AddWithValue("$error", printed || error is null ? DBNull.Value : error);
            command.Parameters.AddWithValue("$target", printTarget is null ? DBNull.Value : printTarget);
            command.Parameters.AddWithValue("$id", id.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task MarkAllKitchenItemsPrintedAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE RestaurantOrderItems SET KitchenPrintStatus = 'printed', KitchenPrintError = NULL
            WHERE KitchenPrintStatus IS NULL;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Atribui um número sequencial de impressão, reiniciado no fechamento do caixa.
    /// Todos os itens do mesmo envio recebem o mesmo número — inclusive cozinha e bar.
    /// </summary>
    public async Task<int> AssignPrintBatchNumberAsync(
        IEnumerable<Guid> itemIds,
        CancellationToken cancellationToken = default)
    {
        var ids = itemIds.ToArray();
        if (ids.Length == 0)
            return 0;

        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        int? existingNumber = null;
        var unnumbered = 0;
        foreach (var id in ids)
        {
            await using var read = connection.CreateCommand();
            read.Transaction = (SqliteTransaction)transaction;
            read.CommandText = "SELECT PrintBatchNumber FROM RestaurantOrderItems WHERE Id = $id;";
            read.Parameters.AddWithValue("$id", id.ToString());
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("Item de impressão não encontrado.");
            if (reader.IsDBNull(0))
                unnumbered++;
            else
            {
                var current = (int)reader.GetInt64(0);
                if (existingNumber is not null && existingNumber != current)
                    throw new InvalidOperationException("Os itens pertencem a diferentes envios de impressão.");
                existingNumber = current;
            }
        }
        if (existingNumber is not null)
        {
            if (unnumbered != 0)
                throw new InvalidOperationException("Não é possível misturar itens já numerados e novos no mesmo envio.");
            await transaction.CommitAsync(cancellationToken);
            return existingNumber.Value;
        }
        var number = await NextPrintBatchNumberAsync(connection, (SqliteTransaction)transaction, cancellationToken);
        foreach (var id in ids)
        {
            await using var assign = connection.CreateCommand();
            assign.Transaction = (SqliteTransaction)transaction;
            assign.CommandText = """
                UPDATE RestaurantOrderItems SET PrintBatchNumber = $number
                WHERE Id = $id AND PrintBatchNumber IS NULL;
                """;
            assign.Parameters.AddWithValue("$number", number);
            assign.Parameters.AddWithValue("$id", id.ToString());
            await assign.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return number;
    }

    private static async Task<int> NextPrintBatchNumberAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        int number;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = (SqliteTransaction)transaction;
            read.CommandText = "SELECT Value FROM AppSettings WHERE Key = 'printBatch.counter';";
            var stored = await read.ExecuteScalarAsync(cancellationToken) as string;
            var storedNumber = stored?.Split(':').LastOrDefault();
            if (int.TryParse(storedNumber, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                number = parsed + 1;
            else
                number = 1;
        }

        await using (var write = connection.CreateCommand())
        {
            write.Transaction = (SqliteTransaction)transaction;
            write.CommandText = """
                INSERT INTO AppSettings (Key, Value) VALUES ('printBatch.counter', $value)
                ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
                """;
            write.Parameters.AddWithValue("$value", number.ToString(CultureInfo.InvariantCulture));
            await write.ExecuteNonQueryAsync(cancellationToken);
        }

        return number;
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
        await AddOrderItemAsync(tableId, menuItemId, quantity, employeeId, null, cancellationToken);
    }

    public async Task AddSizedOrderItemAsync(
        Guid tableId,
        Guid menuItemId,
        decimal quantity,
        Guid? employeeId,
        string? observation,
        IReadOnlyList<RestaurantOrderSelection>? selections,
        string size,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await AddOrderItemInTransactionAsync(connection, transaction, tableId, menuItemId, quantity, employeeId,
            NormalizeObservation(observation), null, selections, size, null, null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task AddOrderItemAsync(
        Guid tableId,
        Guid menuItemId,
        decimal quantity,
        Guid? employeeId,
        string? observation,
        IReadOnlyList<RestaurantOrderSelection>? selections,
        CancellationToken cancellationToken = default)
    {
        await AddOrderItemCoreAsync(
            tableId, menuItemId, quantity, employeeId, NormalizeObservation(observation), null, null, null, selections, cancellationToken);
    }

    public async Task AddOrderItemAsync(
        Guid tableId,
        Guid menuItemId,
        decimal quantity,
        Guid? employeeId,
        string? observation,
        CancellationToken cancellationToken = default)
    {
        await AddOrderItemCoreAsync(
            tableId, menuItemId, quantity, employeeId, NormalizeObservation(observation), null, null, null, null, cancellationToken);
    }

    public async Task<WaiterCommandResult> ProcessWaiterCommandAsync(
        WaiterCommandRecord command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.Id))
            throw new ArgumentException("O comando do garçom não possui identificador.", nameof(command));

        var previousResult = await GetWaiterCommandResultAsync(command.Id, cancellationToken);
        if (previousResult is not null)
            return previousResult;

        try
        {
            if (!Guid.TryParse(command.Id, out _))
                throw new InvalidOperationException("O identificador do comando não é um GUID válido.");
            if (!string.Equals(command.Status, "pending", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("O comando não está pendente.");
            if (string.IsNullOrWhiteSpace(command.EmployeeUid))
                throw new InvalidOperationException("O comando não identifica o garçom.");
            if (!Guid.TryParse(command.AreaId, out var areaId) ||
                !Guid.TryParse(command.TableId, out var tableId))
                throw new InvalidOperationException("A área ou mesa do comando não possui um identificador válido.");

            var employee = (await GetEmployeesAsync(cancellationToken))
                .SingleOrDefault(candidate =>
                    candidate.FirebaseUid?.Equals(command.EmployeeUid, StringComparison.Ordinal) == true ||
                    string.Equals(
                        CreateLocalWaiterUid(candidate.Id),
                        command.EmployeeUid,
                        StringComparison.OrdinalIgnoreCase));
            if (employee is null || !employee.IsActive)
                throw new InvalidOperationException("O garçom vinculado ao comando não está cadastrado ou ativo nesta loja.");

            var authorizedAreaIds = (employee.AreaIds ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => Guid.TryParse(value, out var id) ? id : Guid.Empty)
                .ToHashSet();
            if (!authorizedAreaIds.Contains(areaId))
                throw new InvalidOperationException("O garçom não está autorizado a lançar pedidos nesta área.");

            switch (command.Type)
            {
                case "submitOrder":
                    if (command.Items is null || command.Items.Count is < 1 or > 100)
                        throw new InvalidOperationException("O pedido deve conter entre 1 e 100 itens.");
                    var customerName = (command.CustomerName ?? string.Empty).Trim();
                    if (customerName.Length > 100 || customerName.Any(char.IsControl))
                        throw new InvalidOperationException("O nome do cliente deve ter no máximo 100 caracteres, sem quebras de linha.");
                    await using (var connection = database.CreateConnection())
                    {
                        await connection.OpenAsync(cancellationToken);
                        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
                        await VerifyWaiterCommandAuthorizationAsync(connection, transaction, employee.Id, command.EmployeeUid, areaId, cancellationToken);
                        var printNumber = await NextPrintBatchNumberAsync(connection, transaction, cancellationToken);
                        Guid orderId = Guid.Empty;
                        foreach (var line in command.Items)
                        {
                            if (line is null || !Guid.TryParse(line.MenuItemId, out var itemId))
                                throw new InvalidOperationException("O produto do pedido não possui um identificador válido.");
                            if (line.Quantity is <= 0 or > 99 || decimal.Truncate(line.Quantity) != line.Quantity)
                                throw new InvalidOperationException("A quantidade deve ser um inteiro entre 1 e 99.");
                            if (line.Options is { Count: > 20 })
                                throw new InvalidOperationException("O item pode ter no máximo 20 variações.");
                            var selections = line.Options?.Select(option =>
                                option is not null && Guid.TryParse(option.OptionId, out var optionId) &&
                                option.Quantity > 0 && decimal.Truncate(option.Quantity) == option.Quantity
                                    ? new RestaurantOrderSelection(optionId, option.Quantity)
                                    : throw new InvalidOperationException("Variação ou quantidade inválida.")).ToArray();
                            orderId = await AddOrderItemInTransactionAsync(
                                connection, transaction, tableId, itemId, line.Quantity, employee.Id,
                                NormalizeObservation(line.Observation), areaId, selections, line.Size, printNumber, command.Id, cancellationToken);
                        }
                        if (customerName.Length > 0)
                        {
                            await using var updateCustomer = connection.CreateCommand();
                            updateCustomer.Transaction = transaction;
                            updateCustomer.CommandText = """
                                UPDATE RestaurantOrders SET CustomerName = $name
                                WHERE Id = $id AND CustomerName = '';
                                """;
                            updateCustomer.Parameters.AddWithValue("$name", customerName);
                            updateCustomer.Parameters.AddWithValue("$id", orderId.ToString());
                            await updateCustomer.ExecuteNonQueryAsync(cancellationToken);
                        }
                        await SaveAppliedWaiterCommandAsync(connection, transaction, command.Id, orderId.ToString(), cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                        return new WaiterCommandResult("applied", null, orderId);
                    }
                case "addItem":
                    if (!Guid.TryParse(command.MenuItemId, out var menuItemId))
                        throw new InvalidOperationException("O produto do comando não possui um identificador válido.");
                    if (command.Quantity is <= 0 or > 99)
                        throw new InvalidOperationException("A quantidade do item deve ser maior que zero e não pode ultrapassar 99.");
                    var addedOrderId = await AddOrderItemCoreAsync(
                        tableId,
                        menuItemId,
                        command.Quantity,
                        employee.Id,
                        NormalizeObservation(command.Observation),
                        command.Id,
                        command.EmployeeUid,
                        areaId,
                        command.Options?.Select(option =>
                            Guid.TryParse(option.OptionId, out var optionId)
                                ? new RestaurantOrderSelection(optionId, option.Quantity, option.ExtraPrice)
                                : throw new InvalidOperationException("A variação do comando não possui um identificador válido.")).ToArray(),
                        cancellationToken);
                    return new WaiterCommandResult("applied", null, addedOrderId);

                case "removeItem":
                case "removeOne":
                    if (!Guid.TryParse(command.OrderItemId, out var removeItemId))
                        throw new InvalidOperationException("O item da comanda não possui um identificador válido.");
                    var removedOrderId = await RemoveOrderItemCoreAsync(
                        tableId,
                        removeItemId,
                        command.Id,
                        employee.Id,
                        command.EmployeeUid,
                        areaId,
                        cancellationToken,
                        command.Type == "removeOne");
                    return new WaiterCommandResult("applied", null, removedOrderId);

                case "updateObservation":
                case "updateOneObservation":
                    if (!Guid.TryParse(command.OrderItemId, out var observationItemId))
                        throw new InvalidOperationException("O item da comanda não possui um identificador válido.");
                    if (command.Observation is null)
                        throw new InvalidOperationException("O comando não informa a observação; envie texto vazio para removê-la.");
                    var updatedOrderId = await UpdateOrderItemObservationCoreAsync(
                        tableId,
                        observationItemId,
                        NormalizeObservation(command.Observation),
                        command.Id,
                        employee.Id,
                        command.EmployeeUid,
                        areaId,
                        cancellationToken,
                        command.Type == "updateOneObservation");
                    return new WaiterCommandResult("applied", null, updatedOrderId);

                default:
                    throw new InvalidOperationException("Tipo de comando do garçom não suportado.");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException or ArgumentException)
        {
            return await SaveRejectedWaiterCommandAsync(command.Id, exception.Message, cancellationToken);
        }
    }

    private async Task<WaiterCommandResult?> GetWaiterCommandResultAsync(
        string commandId,
        CancellationToken cancellationToken)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Status, Message, OrderId
            FROM RestaurantWaiterCommands WHERE CommandId = $commandId;
            """;
        command.Parameters.AddWithValue("$commandId", commandId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        return new WaiterCommandResult(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)));
    }

    private async Task<WaiterCommandResult> SaveRejectedWaiterCommandAsync(
        string commandId,
        string message,
        CancellationToken cancellationToken)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO RestaurantWaiterCommands
                    (CommandId, Status, Message, OrderId, ProcessedAt)
                VALUES ($commandId, 'rejected', $message, NULL, $processedAt);
                """;
            insert.Parameters.AddWithValue("$commandId", commandId);
            insert.Parameters.AddWithValue("$message", message);
            insert.Parameters.AddWithValue("$processedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var read = connection.CreateCommand();
        read.Transaction = (SqliteTransaction)transaction;
        read.CommandText = """
            SELECT Status, Message, OrderId
            FROM RestaurantWaiterCommands WHERE CommandId = $commandId;
            """;
        read.Parameters.AddWithValue("$commandId", commandId);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Não foi possível persistir o resultado do comando do garçom.");
        var result = new WaiterCommandResult(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)));
        await reader.DisposeAsync();
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<Guid> AddOrderItemCoreAsync(
        Guid tableId,
        Guid menuItemId,
        decimal quantity,
        Guid? employeeId,
        string? observation,
        string? waiterCommandId,
        string? waiterUid,
        Guid? authorizedAreaId,
        IReadOnlyList<RestaurantOrderSelection>? selections,
        CancellationToken cancellationToken)
    {
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), "A quantidade deve ser maior que zero.");
        selections = selections?.Where(selection => selection.Quantity != 0).ToArray();
        if (selections is { Count: > 0 })
        {
            if (selections.Any(selection => selection.Quantity < 0 || selection.ExtraPrice is < 0 or > 9999))
                throw new InvalidOperationException("Quantidade ou valor extra da variação inválido.");
            if (selections.GroupBy(selection => selection.OptionId).Any(group => group.Count() > 1))
                throw new InvalidOperationException("Variação repetida no pedido.");
            if (selections.Sum(selection => selection.Quantity) > quantity)
                throw new InvalidOperationException("A soma das variações não pode ser maior que a quantidade do item.");
        }
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (waiterCommandId is not null)
        {
            if (employeeId is null || string.IsNullOrWhiteSpace(waiterUid) || authorizedAreaId is null)
                throw new InvalidOperationException("O comando do garçom não possui autorização local completa.");
            await VerifyWaiterCommandAuthorizationAsync(
                connection, (SqliteTransaction)transaction, employeeId.Value, waiterUid, authorizedAreaId.Value, cancellationToken);
        }

        var result = await AddOrderItemInTransactionAsync(
            connection, (SqliteTransaction)transaction, tableId, menuItemId, quantity, employeeId,
            observation, authorizedAreaId, selections, null, null, null, cancellationToken);
        if (waiterCommandId is not null)
            await SaveAppliedWaiterCommandAsync(connection, (SqliteTransaction)transaction, waiterCommandId, result.ToString(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<Guid> AddOrderItemInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid tableId,
        Guid menuItemId,
        decimal quantity,
        Guid? employeeId,
        string? observation,
        Guid? authorizedAreaId,
        IReadOnlyList<RestaurantOrderSelection>? selections,
        string? size,
        int? printBatchNumber,
        string? printBatchKey,
        CancellationToken cancellationToken)
    {
        if (quantity <= 0)
            throw new InvalidOperationException("A quantidade deve ser maior que zero.");
        if (selections is { Count: > 0 })
        {
            if (selections.Any(selection => selection.Quantity <= 0 || selection.ExtraPrice is < 0 or > 9999) ||
                selections.GroupBy(selection => selection.OptionId).Any(group => group.Count() > 1) ||
                selections.Sum(selection => selection.Quantity) > quantity)
                throw new InvalidOperationException("As quantidades ou variações informadas são inválidas.");
        }
        string? orderId;
        string menuName;
        decimal unitPrice;
        string? inventoryProductId;
        decimal inventoryQuantityPerUnit;
        await using (var item = connection.CreateCommand())
        {
            item.Transaction = (SqliteTransaction)transaction;
            item.CommandText = """
                SELECT o.Id, m.Name, m.Price, m.InventoryProductId, m.InventoryQuantityPerUnit, m.PriceM, m.PriceG
                FROM RestaurantTables t
                JOIN RestaurantAreas a ON a.Id = t.AreaId AND a.IsActive = 1
                JOIN RestaurantMenuItems m ON m.Id = $menuItemId AND m.IsActive = 1
                LEFT JOIN RestaurantOrders o ON o.TableId = t.Id AND o.Status = 'Open'
                WHERE t.Id = $tableId AND t.IsActive = 1
                  AND ($areaId IS NULL OR t.AreaId = $areaId);
                """;
            item.Parameters.AddWithValue("$tableId", tableId.ToString());
            item.Parameters.AddWithValue("$areaId", authorizedAreaId?.ToString() ?? (object)DBNull.Value);
            item.Parameters.AddWithValue("$menuItemId", menuItemId.ToString());
            await using var reader = await item.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("Não foi possível adicionar: confira se a mesa existe e o item está ativo.");
            orderId = reader.IsDBNull(0) ? null : reader.GetString(0);
            menuName = reader.GetString(1);
            unitPrice = decimal.Parse(reader.GetString(2), CultureInfo.InvariantCulture);
            inventoryProductId = reader.IsDBNull(3) ? null : reader.GetString(3);
            inventoryQuantityPerUnit = decimal.Parse(reader.GetString(4), CultureInfo.InvariantCulture);
            var priceM = reader.IsDBNull(5) ? (decimal?)null : decimal.Parse(reader.GetString(5), CultureInfo.InvariantCulture);
            var priceG = reader.IsDBNull(6) ? (decimal?)null : decimal.Parse(reader.GetString(6), CultureInfo.InvariantCulture);
            if (size is not null)
            {
                var sizePrice = size switch { "M" => priceM, "G" => priceG, _ => null };
                if (sizePrice is null)
                    throw new InvalidOperationException($"Tamanho '{size}' indisponível para '{menuName}'.");
                unitPrice = sizePrice.Value;
                menuName = $"{menuName} ({size})";
            }
            else if (priceM is not null || priceG is not null)
                throw new InvalidOperationException($"Selecione o tamanho de '{menuName}'.");
        }

        if (orderId is null)
        {
            orderId = Guid.NewGuid().ToString();
            var openedAt = DateTimeOffset.UtcNow;
            var dailyNumber = await GetNextDailyOrderNumberAsync(
                connection, (SqliteTransaction)transaction, cancellationToken);
            await using var createOrder = connection.CreateCommand();
            createOrder.Transaction = (SqliteTransaction)transaction;
            createOrder.CommandText = """
                INSERT INTO RestaurantOrders (Id, TableId, EmployeeId, OpenedAt, Status, DailyNumber)
                VALUES ($id, $tableId, $employeeId, $openedAt, 'Open', $dailyNumber);
                """;
            createOrder.Parameters.AddWithValue("$id", orderId);
            createOrder.Parameters.AddWithValue("$tableId", tableId.ToString());
            createOrder.Parameters.AddWithValue("$employeeId", employeeId?.ToString() ?? (object)DBNull.Value);
            createOrder.Parameters.AddWithValue("$openedAt", openedAt.ToString("O", CultureInfo.InvariantCulture));
            createOrder.Parameters.AddWithValue("$dailyNumber", dailyNumber);
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

        var lines = new List<(string Name, decimal Quantity, decimal UnitPrice, string VariantKey)>();
        var selectedTotal = 0m;
        if (selections is { Count: > 0 })
        {
            var menuOptions = new Dictionary<Guid, (string Name, decimal ExtraPrice)>();
            await using (var optionsQuery = connection.CreateCommand())
            {
                optionsQuery.Transaction = (SqliteTransaction)transaction;
                optionsQuery.CommandText = "SELECT Id, Name, ExtraPrice FROM RestaurantMenuItemOptions WHERE MenuItemId = $menuItemId;";
                optionsQuery.Parameters.AddWithValue("$menuItemId", menuItemId.ToString());
                await using var optionsReader = await optionsQuery.ExecuteReaderAsync(cancellationToken);
                while (await optionsReader.ReadAsync(cancellationToken))
                    menuOptions[Guid.Parse(optionsReader.GetString(0))] = (
                        optionsReader.GetString(1),
                        decimal.Parse(optionsReader.GetString(2), CultureInfo.InvariantCulture));
            }
            foreach (var selection in selections)
            {
                if (!menuOptions.TryGetValue(selection.OptionId, out var option))
                    throw new InvalidOperationException($"A variação não pertence ao item '{menuName}'.");
                lines.Add(($"{menuName} ({option.Name})", selection.Quantity, unitPrice + (selection.ExtraPrice ?? option.ExtraPrice),
                    $"{size ?? "-"}:{selection.OptionId:D}"));
                selectedTotal += selection.Quantity;
            }
        }
        if (quantity - selectedTotal > 0)
            lines.Insert(0, (menuName, quantity - selectedTotal, unitPrice, $"{size ?? "-"}:-"));

        foreach (var line in lines)
        {
            var orderItemId = Guid.NewGuid();
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = (SqliteTransaction)transaction;
                insert.CommandText = """
                    INSERT INTO RestaurantOrderItems
                        (Id, OrderId, MenuItemId, MenuItemName, Quantity, UnitPrice, InventoryProductId, InventoryQuantityPerUnit, Observation, PrintBatchNumber, PrintBatchKey, SendingEmployeeId, VariantKey)
                    VALUES ($id, $orderId, $menuItemId, $name, $quantity, $unitPrice, $inventoryProductId, $inventoryQuantity, $observation, $batchNumber, $batchKey, $sendingEmployee, $variantKey);
                    """;
                insert.Parameters.AddWithValue("$id", orderItemId.ToString());
                insert.Parameters.AddWithValue("$orderId", orderId);
                insert.Parameters.AddWithValue("$menuItemId", menuItemId.ToString());
                insert.Parameters.AddWithValue("$name", line.Name);
                insert.Parameters.AddWithValue("$quantity", line.Quantity.ToString(CultureInfo.InvariantCulture));
                insert.Parameters.AddWithValue("$unitPrice", line.UnitPrice.ToString(CultureInfo.InvariantCulture));
                insert.Parameters.AddWithValue("$inventoryProductId", inventoryProductId ?? (object)DBNull.Value);
                insert.Parameters.AddWithValue("$inventoryQuantity", inventoryQuantityPerUnit.ToString(CultureInfo.InvariantCulture));
                insert.Parameters.AddWithValue("$observation", observation ?? string.Empty);
                insert.Parameters.AddWithValue("$batchNumber", printBatchNumber ?? (object)DBNull.Value);
                insert.Parameters.AddWithValue("$batchKey", printBatchKey ?? (object)DBNull.Value);
                insert.Parameters.AddWithValue("$sendingEmployee", employeeId?.ToString() ?? (object)DBNull.Value);
                insert.Parameters.AddWithValue("$variantKey", line.VariantKey);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            await SqliteSyncOutboxRepository.EnqueueAsync(
                connection,
                (SqliteTransaction)transaction,
                "restaurant",
                "order-item",
                orderItemId.ToString(),
                new { orderId, tableId, menuItemId, menuName = line.Name, quantity = line.Quantity, unitPrice = line.UnitPrice, observation },
                cancellationToken);
        }
        return Guid.Parse(orderId);
    }

    private static List<RestaurantMenuOptionRecord>? NormalizeOptions(IReadOnlyList<RestaurantMenuOptionRecord>? options)
    {
        if (options is null)
            return null;
        if (options.Count > 20)
            throw new InvalidOperationException("Cada item pode ter no máximo 20 variações.");
        var result = new List<RestaurantMenuOptionRecord>();
        foreach (var option in options)
        {
            var name = option.Name?.Trim() ?? string.Empty;
            if (name.Length is 0 or > 60)
                throw new InvalidOperationException("O nome da variação deve ter entre 1 e 60 caracteres.");
            if (option.ExtraPrice is < 0 or > 9999)
                throw new InvalidOperationException($"O valor extra da variação '{name}' deve estar entre 0 e 9.999.");
            if (result.Any(existing => existing.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)))
                throw new InvalidOperationException($"A variação '{name}' está repetida.");
            result.Add(option with { Name = name, ExtraPrice = decimal.Round(option.ExtraPrice, 2) });
        }
        return result;
    }

    private static string? NormalizeObservation(string? observation)
    {
        if (string.IsNullOrWhiteSpace(observation))
            return null;
        var normalized = observation.Trim();
        if (normalized.Length > 250)
            throw new InvalidOperationException("A observação do item não pode ultrapassar 250 caracteres.");
        return normalized;
    }

    public async Task RemoveOrderItemAsync(
        Guid tableId,
        Guid orderItemId,
        CancellationToken cancellationToken = default)
    {
        await RemoveOrderItemCoreAsync(tableId, orderItemId, null, null, null, null, cancellationToken);
    }

    private async Task<Guid> RemoveOrderItemCoreAsync(
        Guid tableId,
        Guid orderItemId,
        string? waiterCommandId,
        Guid? employeeId,
        string? waiterUid,
        Guid? authorizedAreaId,
        CancellationToken cancellationToken,
        bool unitOnly = false)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        if (waiterCommandId is not null)
        {
            if (employeeId is null || string.IsNullOrWhiteSpace(waiterUid) || authorizedAreaId is null)
                throw new InvalidOperationException("O comando do garçom não possui autorização local completa.");
            await VerifyWaiterCommandAuthorizationAsync(
                connection, (SqliteTransaction)transaction, employeeId.Value, waiterUid, authorizedAreaId.Value, cancellationToken);
        }

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
                JOIN RestaurantTables t ON t.Id = o.TableId AND t.IsActive = 1
                JOIN RestaurantAreas a ON a.Id = t.AreaId AND a.IsActive = 1
                LEFT JOIN RestaurantMenuItems m ON m.Id = i.MenuItemId
                WHERE o.TableId = $tableId AND o.Status = 'Open' AND i.Id = $itemId
                  AND ($areaId IS NULL OR t.AreaId = $areaId);
                """;
            find.Parameters.AddWithValue("$tableId", tableId.ToString());
            find.Parameters.AddWithValue("$itemId", orderItemId.ToString());
            find.Parameters.AddWithValue("$areaId", authorizedAreaId?.ToString() ?? (object)DBNull.Value);
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

        if (unitOnly)
        {
            if (quantity < 1 || decimal.Truncate(quantity) != quantity)
                throw new InvalidOperationException("Não é possível remover uma unidade de uma quantidade fracionária.");
            await QueueItemChangeNoticeAsync(connection, (SqliteTransaction)transaction, orderItemId,
                waiterCommandId!, "remove", null, cancellationToken);
        }

        if (inventoryProductId is not null && inventoryQuantityPerUnit > 0)
        {
            var restoredQuantity = (unitOnly ? 1m : quantity) * inventoryQuantityPerUnit;
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
            remove.CommandText = unitOnly && quantity > 1
                ? "UPDATE RestaurantOrderItems SET Quantity = $quantity WHERE Id = $itemId;"
                : "DELETE FROM RestaurantOrderItems WHERE Id = $itemId;";
            if (unitOnly && quantity > 1)
                remove.Parameters.AddWithValue("$quantity", (quantity - 1).ToString(CultureInfo.InvariantCulture));
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
            new { orderId, tableId, orderItemId, itemName, quantity = unitOnly ? 1m : quantity },
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

        if (waiterCommandId is not null)
        {
            await SaveAppliedWaiterCommandAsync(
                connection, (SqliteTransaction)transaction, waiterCommandId, orderId, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return Guid.Parse(orderId);
    }

    private async Task<Guid> UpdateOrderItemObservationCoreAsync(
        Guid tableId,
        Guid orderItemId,
        string? observation,
        string waiterCommandId,
        Guid employeeId,
        string waiterUid,
        Guid authorizedAreaId,
        CancellationToken cancellationToken,
        bool unitOnly = false)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await VerifyWaiterCommandAuthorizationAsync(
            connection, (SqliteTransaction)transaction, employeeId, waiterUid, authorizedAreaId, cancellationToken);

        string orderId;
        string itemName;
        decimal quantity;
        await using (var find = connection.CreateCommand())
        {
            find.Transaction = (SqliteTransaction)transaction;
            find.CommandText = """
                SELECT o.Id, i.MenuItemName, i.Quantity
                FROM RestaurantOrders o
                JOIN RestaurantTables t ON t.Id = o.TableId AND t.IsActive = 1
                JOIN RestaurantAreas a ON a.Id = t.AreaId AND a.IsActive = 1
                JOIN RestaurantOrderItems i ON i.OrderId = o.Id
                WHERE o.TableId = $tableId AND o.Status = 'Open' AND i.Id = $itemId
                  AND t.AreaId = $areaId;
                """;
            find.Parameters.AddWithValue("$tableId", tableId.ToString());
            find.Parameters.AddWithValue("$itemId", orderItemId.ToString());
            find.Parameters.AddWithValue("$areaId", authorizedAreaId.ToString());
            await using var reader = await find.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("O item selecionado não pertence a uma comanda aberta desta mesa.");
            orderId = reader.GetString(0);
            itemName = reader.GetString(1);
            quantity = decimal.Parse(reader.GetString(2), CultureInfo.InvariantCulture);
        }

        await using (var paid = connection.CreateCommand())
        {
            paid.Transaction = (SqliteTransaction)transaction;
            paid.CommandText = "SELECT COALESCE(SUM(CAST(Amount AS REAL)), 0) FROM RestaurantPayments WHERE OrderId = $orderId;";
            paid.Parameters.AddWithValue("$orderId", orderId);
            if (Convert.ToDecimal(await paid.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) > 0)
                throw new InvalidOperationException("Não é possível alterar observações depois de registrar um pagamento parcial.");
        }

        var changedItemId = orderItemId;
        if (unitOnly)
        {
            if (quantity < 1 || decimal.Truncate(quantity) != quantity)
                throw new InvalidOperationException("Não é possível editar uma unidade de uma quantidade fracionária.");
            await QueueItemChangeNoticeAsync(connection, (SqliteTransaction)transaction, orderItemId,
                waiterCommandId, "observation", observation, cancellationToken);
            if (quantity > 1)
            {
                changedItemId = Guid.NewGuid();
                await using var split = connection.CreateCommand();
                split.Transaction = (SqliteTransaction)transaction;
                split.CommandText = """
                    INSERT INTO RestaurantOrderItems
                      (Id, OrderId, MenuItemId, MenuItemName, Quantity, UnitPrice, InventoryProductId,
                       InventoryQuantityPerUnit, Observation, KitchenPrintStatus, KitchenPrintError, KitchenPrintTarget,
                       PrintBatchNumber, SendingEmployeeId, PrintBatchKey, VariantKey)
                    SELECT $newId, OrderId, MenuItemId, MenuItemName, '1', UnitPrice, InventoryProductId,
                           InventoryQuantityPerUnit, Observation, KitchenPrintStatus, KitchenPrintError, KitchenPrintTarget,
                           PrintBatchNumber, SendingEmployeeId, PrintBatchKey, VariantKey
                    FROM RestaurantOrderItems WHERE Id = $itemId AND OrderId = $orderId;
                    UPDATE RestaurantOrderItems SET Quantity = $quantity WHERE Id = $itemId AND OrderId = $orderId;
                    """;
                split.Parameters.AddWithValue("$newId", changedItemId.ToString());
                split.Parameters.AddWithValue("$itemId", orderItemId.ToString());
                split.Parameters.AddWithValue("$orderId", orderId);
                split.Parameters.AddWithValue("$quantity", (quantity - 1).ToString(CultureInfo.InvariantCulture));
                if (await split.ExecuteNonQueryAsync(cancellationToken) != 2)
                    throw new InvalidOperationException("O item da comanda foi alterado antes de salvar a observação.");
            }
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = (SqliteTransaction)transaction;
            update.CommandText = """
                UPDATE RestaurantOrderItems SET Observation = $observation
                WHERE Id = $itemId AND OrderId = $orderId;
                """;
            update.Parameters.AddWithValue("$observation", observation ?? string.Empty);
            update.Parameters.AddWithValue("$itemId", changedItemId.ToString());
            update.Parameters.AddWithValue("$orderId", orderId);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("O item da comanda foi alterado antes de salvar a observação.");
        }

        await SqliteSyncOutboxRepository.EnqueueAsync(
            connection,
            (SqliteTransaction)transaction,
            "restaurant",
            "order-item-observation-updated",
            changedItemId.ToString(),
            new { orderId, tableId, orderItemId = changedItemId, itemName, observation },
            cancellationToken);
        await SaveAppliedWaiterCommandAsync(
            connection, (SqliteTransaction)transaction, waiterCommandId, orderId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Guid.Parse(orderId);
    }

    private static async Task QueueItemChangeNoticeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid itemId,
        string commandId,
        string action,
        string? observation,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO RestaurantItemChangeNotices
              (CommandId, OrderId, TableNumber, AreaName, ItemName, PrintTarget, PrintBatchNumber,
               Action, OldObservation, NewObservation, CreatedAt)
            SELECT $commandId, o.Id, t.Number, a.Name, i.MenuItemName, i.KitchenPrintTarget, i.PrintBatchNumber,
                   $action, COALESCE(i.Observation, ''), $observation, $createdAt
            FROM RestaurantOrderItems i
            JOIN RestaurantOrders o ON o.Id = i.OrderId
            JOIN RestaurantTables t ON t.Id = o.TableId
            JOIN RestaurantAreas a ON a.Id = t.AreaId
            WHERE i.Id = $itemId AND i.KitchenPrintStatus = 'printed'
              AND i.KitchenPrintTarget IN ('kitchen', 'bar');
            """;
        command.Parameters.AddWithValue("$commandId", commandId);
        command.Parameters.AddWithValue("$itemId", itemId.ToString());
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$observation", observation ?? string.Empty);
        command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("O item ainda não foi confirmado como impresso ou não possui destino válido; solicite o ajuste ao caixa.");
    }

    public async Task<IReadOnlyList<RestaurantItemChangeNotice>> GetItemChangeNoticesAsync(
        bool failedOnly, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CommandId, OrderId, TableNumber, AreaName, ItemName, PrintTarget, PrintBatchNumber,
                   Action, OldObservation, NewObservation, CreatedAt, Status, Error
            FROM RestaurantItemChangeNotices WHERE Status = $status ORDER BY rowid;
            """;
        command.Parameters.AddWithValue("$status", failedOnly ? "failed" : "pending");
        var notices = new List<RestaurantItemChangeNotice>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            notices.Add(new RestaurantItemChangeNotice(
                reader.GetString(0), Guid.Parse(reader.GetString(1)), reader.GetInt32(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.GetString(7), reader.GetString(8), reader.GetString(9),
                DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture), reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12)));
        return notices;
    }

    public async Task MarkItemChangeNoticeAsync(
        string commandId, bool printed, string? error = null, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE RestaurantItemChangeNotices SET Status = $status, Error = $error
            WHERE CommandId = $id AND Status IN ('pending', 'failed');
            """;
        command.Parameters.AddWithValue("$id", commandId);
        command.Parameters.AddWithValue("$status", printed ? "printed" : "failed");
        command.Parameters.AddWithValue("$error", error is null ? DBNull.Value : error);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("O aviso de impressão não está mais pendente.");
    }

    private static async Task VerifyWaiterCommandAuthorizationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid employeeId,
        string waiterUid,
        Guid authorizedAreaId,
        CancellationToken cancellationToken)
    {
        await using var verifyWaiter = connection.CreateCommand();
        verifyWaiter.Transaction = transaction;
        verifyWaiter.CommandText = """
            SELECT FirebaseUid, IsActive, AreaIds
            FROM RestaurantEmployees WHERE Id = $employeeId;
            """;
        verifyWaiter.Parameters.AddWithValue("$employeeId", employeeId.ToString());
        await using var waiterReader = await verifyWaiter.ExecuteReaderAsync(cancellationToken);
        if (!await waiterReader.ReadAsync(cancellationToken) ||
            !waiterReader.GetBoolean(1))
            throw new InvalidOperationException("O garçom foi desativado ou seu vínculo Firebase foi alterado.");
        var firebaseUid = waiterReader.IsDBNull(0) ? null : waiterReader.GetString(0);
        var isAuthorized =
            firebaseUid?.Equals(waiterUid, StringComparison.Ordinal) == true ||
            string.Equals(CreateLocalWaiterUid(employeeId), waiterUid, StringComparison.OrdinalIgnoreCase);
        if (!isAuthorized)
            throw new InvalidOperationException("O garçom foi desativado ou seu vínculo Firebase foi alterado.");
        var currentAreaIds = waiterReader.IsDBNull(2) ? string.Empty : waiterReader.GetString(2);
        await waiterReader.DisposeAsync();
        if (!currentAreaIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(value => Guid.TryParse(value, out var id) && id == authorizedAreaId))
            throw new InvalidOperationException("O acesso do garçom a esta área foi removido.");
    }

    private static async Task SaveAppliedWaiterCommandAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string commandId,
        string orderId,
        CancellationToken cancellationToken)
    {
        await using var processed = connection.CreateCommand();
        processed.Transaction = transaction;
        processed.CommandText = """
            INSERT INTO RestaurantWaiterCommands (CommandId, Status, Message, OrderId, ProcessedAt)
            VALUES ($commandId, 'applied', NULL, $orderId, $processedAt);
            """;
        processed.Parameters.AddWithValue("$commandId", commandId);
        processed.Parameters.AddWithValue("$orderId", orderId);
        processed.Parameters.AddWithValue("$processedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await processed.ExecuteNonQueryAsync(cancellationToken);
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
        CancellationToken cancellationToken = default) =>
        await PayOrderCoreAsync(tableId, paymentMethod, amount, waiterServiceChargeEnabled, null, cancellationToken);

    public async Task<RestaurantPaymentRecord> PayOrderItemsAsync(
        Guid tableId,
        PaymentMethod paymentMethod,
        IReadOnlyList<RestaurantItemPaymentSelection> selections,
        bool waiterServiceChargeEnabled,
        decimal expectedAmount,
        CancellationToken cancellationToken = default) =>
        await PayOrderCoreAsync(tableId, paymentMethod, expectedAmount, waiterServiceChargeEnabled, selections, cancellationToken);

    private async Task<RestaurantPaymentRecord> PayOrderCoreAsync(
        Guid tableId,
        PaymentMethod paymentMethod,
        decimal amount,
        bool waiterServiceChargeEnabled,
        IReadOnlyList<RestaurantItemPaymentSelection>? selections,
        CancellationToken cancellationToken)
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
        paidCommand.CommandText = """
            SELECT Amount, EXISTS(SELECT 1 FROM RestaurantPaymentItems a WHERE a.PaymentId = p.Id)
            FROM RestaurantPayments p WHERE OrderId = $orderId;
            """;
        paidCommand.Parameters.AddWithValue("$orderId", orderId.ToString());
        decimal paidTotal = 0;
        var hasUnallocatedPayments = false;
        await using (var paidReader = await paidCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await paidReader.ReadAsync(cancellationToken))
            {
                paidTotal += decimal.Parse(paidReader.GetString(0), CultureInfo.InvariantCulture);
                hasUnallocatedPayments |= paidReader.GetInt64(1) == 0;
            }
        }
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
        itemTotals.CommandText = "SELECT Id, MenuItemName, Quantity, UnitPrice FROM RestaurantOrderItems WHERE OrderId = $orderId;";
        itemTotals.Parameters.AddWithValue("$orderId", orderId.ToString());
        decimal total = 0;
        var orderItems = new List<RestaurantOrderItemRecord>();
        await using (var itemsReader = await itemTotals.ExecuteReaderAsync(cancellationToken))
        {
            while (await itemsReader.ReadAsync(cancellationToken))
            {
                var item = new RestaurantOrderItemRecord(Guid.Parse(itemsReader.GetString(0)), itemsReader.GetString(1),
                    decimal.Parse(itemsReader.GetString(2), CultureInfo.InvariantCulture),
                    decimal.Parse(itemsReader.GetString(3), CultureInfo.InvariantCulture));
                orderItems.Add(item);
                total += item.Total;
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
        if (selections is not null)
        {
            if (hasUnallocatedPayments)
                throw new InvalidOperationException("Esta comanda possui pagamento por valor sem vínculo com itens. Continue recebendo por valor.");
            if (selections.Count == 0 || selections.Any(s => s.Quantity <= 0) ||
                selections.Select(s => s.ItemId).Distinct().Count() != selections.Count)
                throw new InvalidOperationException("Selecione itens distintos e quantidades maiores que zero.");
            await using var allocationCommand = connection.CreateCommand();
            allocationCommand.Transaction = (SqliteTransaction)transaction;
            allocationCommand.CommandText = """
                SELECT a.OrderItemId, a.Quantity FROM RestaurantPaymentItems a
                JOIN RestaurantPayments p ON p.Id = a.PaymentId WHERE p.OrderId = $orderId;
                """;
            allocationCommand.Parameters.AddWithValue("$orderId", orderId.ToString());
            var quantities = new Dictionary<Guid, decimal>();
            await using (var allocationReader = await allocationCommand.ExecuteReaderAsync(cancellationToken))
            {
                while (await allocationReader.ReadAsync(cancellationToken))
                {
                    var itemId = Guid.Parse(allocationReader.GetString(0));
                    quantities[itemId] = quantities.GetValueOrDefault(itemId) +
                        decimal.Parse(allocationReader.GetString(1), CultureInfo.InvariantCulture);
                }
            }
            orderItems = orderItems.Select(item => item with { PaidQuantity = quantities.GetValueOrDefault(item.Id) }).ToList();
            foreach (var selection in selections)
            {
                var item = orderItems.Find(i => i.Id == selection.ItemId);
                if (item is null || selection.Quantity > item.RemainingQuantity)
                    throw new InvalidOperationException("A seleção mudou ou excede a quantidade ainda não paga. Atualize a comanda.");
            }
            var calculation = new RestaurantOrderRecord(orderId, tableId, "", DateTimeOffset.UtcNow,
                orderItems, paidTotal, waiterServiceChargeEnabled);
            var calculatedAmount = calculation.ItemPaymentAmount(selections);
            if (calculatedAmount <= 0)
                throw new InvalidOperationException("A seleção não gera valor a receber. Selecione outros itens ou pague o saldo por valor.");
            if (calculatedAmount != amount)
                throw new InvalidOperationException("O valor dos itens mudou. Atualize a seleção antes de receber.");
        }
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
        if (selections is not null)
        {
            foreach (var selection in selections)
            {
                await using var allocation = connection.CreateCommand();
                allocation.Transaction = (SqliteTransaction)transaction;
                allocation.CommandText = """
                    INSERT INTO RestaurantPaymentItems (PaymentId, OrderItemId, Quantity)
                    VALUES ($paymentId, $itemId, $quantity);
                    """;
                allocation.Parameters.AddWithValue("$paymentId", paymentId.ToString());
                allocation.Parameters.AddWithValue("$itemId", selection.ItemId.ToString());
                allocation.Parameters.AddWithValue("$quantity", selection.Quantity.ToString(CultureInfo.InvariantCulture));
                await allocation.ExecuteNonQueryAsync(cancellationToken);
            }
        }
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
                remainingTotal = newRemaining,
                items = selections
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
                WHERE p.SessionId = s.Id AND p.PaymentMethod <> 'Cash'), 0),
              COALESCE((SELECT SUM(CAST(e.Amount AS REAL)) FROM RestaurantExpenses e
                WHERE e.SessionId = s.Id AND e.PaidFromCash = 1), 0)
            FROM RestaurantCashSessions s WHERE s.ClosedAt IS NULL ORDER BY s.OpenedAt DESC LIMIT 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new RestaurantCashSessionRecord(
                Guid.Parse(reader.GetString(0)),
                DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                decimal.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                Convert.ToDecimal(reader.GetDouble(3), CultureInfo.InvariantCulture),
                Convert.ToDecimal(reader.GetDouble(4), CultureInfo.InvariantCulture),
                Convert.ToDecimal(reader.GetDouble(5), CultureInfo.InvariantCulture))
            : null;
    }

    private static (DateTimeOffset StartUtc, DateTimeOffset EndUtc) ToUtcRange(DateOnly from, DateOnly to)
    {
        var start = from.ToDateTime(TimeOnly.MinValue);
        var end = to.AddDays(1).ToDateTime(TimeOnly.MinValue);
        return (
            new DateTimeOffset(start, TimeZoneInfo.Local.GetUtcOffset(start)).ToUniversalTime(),
            new DateTimeOffset(end, TimeZoneInfo.Local.GetUtcOffset(end)).ToUniversalTime());
    }

    public async Task<RestaurantExpenseRecord> AddExpenseAsync(
        string description,
        string category,
        decimal amount,
        bool paidFromCash,
        CancellationToken cancellationToken = default)
    {
        description = description.Trim();
        category = string.IsNullOrWhiteSpace(category) ? "Geral" : category.Trim();
        if (description.Length is 0 or > 120)
            throw new ArgumentException("Informe a descrição do gasto (até 120 caracteres).");
        if (category.Length > 60)
            throw new ArgumentException("A categoria deve ter até 60 caracteres.");
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "O valor do gasto deve ser maior que zero.");
        var session = paidFromCash ? await GetOpenCashSessionAsync(cancellationToken) : null;
        if (paidFromCash && session is null)
            throw new InvalidOperationException("Abra o caixa para registrar um gasto pago com dinheiro do caixa.");
        var record = new RestaurantExpenseRecord(Guid.NewGuid(), DateTimeOffset.UtcNow, category, description, amount, paidFromCash);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO RestaurantExpenses (Id, CreatedAt, Category, Description, Amount, PaidFromCash, SessionId)
            VALUES ($id, $createdAt, $category, $description, $amount, $cash, $session);
            """;
        command.Parameters.AddWithValue("$id", record.Id.ToString());
        command.Parameters.AddWithValue("$createdAt", record.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$category", category);
        command.Parameters.AddWithValue("$description", description);
        command.Parameters.AddWithValue("$amount", amount.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$cash", paidFromCash ? 1 : 0);
        command.Parameters.AddWithValue("$session", session is null ? DBNull.Value : session.Id.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
        return record;
    }

    public async Task<IReadOnlyList<RestaurantExpenseRecord>> GetExpensesAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        var (startUtc, endUtc) = ToUtcRange(from, to);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, CreatedAt, Category, Description, Amount, PaidFromCash FROM RestaurantExpenses;";
        var result = new List<RestaurantExpenseRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var createdAt = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture);
            if (createdAt < startUtc || createdAt >= endUtc)
                continue;
            result.Add(new RestaurantExpenseRecord(
                Guid.Parse(reader.GetString(0)),
                createdAt,
                reader.GetString(2),
                reader.GetString(3),
                decimal.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                reader.GetInt32(5) == 1));
        }
        return result.OrderByDescending(expense => expense.CreatedAt).ToArray();
    }

    public async Task DeleteExpenseAsync(Guid expenseId, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM RestaurantExpenses WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", expenseId.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<RestaurantDashboard> GetDashboardAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        if (to < from)
            throw new ArgumentException("A data final não pode ser anterior à inicial.");
        var (startUtc, endUtc) = ToUtcRange(from, to);
        var expenses = await GetExpensesAsync(from, to, cancellationToken);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT OrderId, PaidAt, PaymentMethod, Amount FROM RestaurantPayments;";
        var rows = new List<(string OrderId, DateTimeOffset PaidAt, string Method, decimal Amount)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var paidAt = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture);
                if (paidAt >= startUtc && paidAt < endUtc)
                    rows.Add((reader.GetString(0), paidAt, reader.GetString(2), decimal.Parse(reader.GetString(3), CultureInfo.InvariantCulture)));
            }
        }
        var total = rows.Sum(row => row.Amount);
        var orders = rows.Select(row => row.OrderId).Distinct().Count();
        var byDay = rows.GroupBy(row => DateOnly.FromDateTime(row.PaidAt.ToLocalTime().DateTime)).ToDictionary(g => g.Key);
        var days = new List<RestaurantDashboardDay>();
        for (var day = from; day <= to; day = day.AddDays(1))
            days.Add(byDay.TryGetValue(day, out var group)
                ? new RestaurantDashboardDay(day, group.Sum(r => r.Amount), group.Count())
                : new RestaurantDashboardDay(day, 0m, 0));
        var methods = rows
            .GroupBy(row => row.Method)
            .Select(g => new RestaurantDashboardMethod(
                Enum.TryParse<PaymentMethod>(g.Key, out var method) ? method : PaymentMethod.Cash,
                g.Sum(r => r.Amount),
                g.Count()))
            .OrderByDescending(m => m.Total)
            .ToArray();
        return new RestaurantDashboard(
            from, to, total, orders, orders == 0 ? 0m : Math.Round(total / orders, 2), days, methods,
            expenses.Sum(expense => expense.Amount),
            expenses.GroupBy(expense => expense.Category)
                .Select(g => new RestaurantDashboardCategory(g.Key, g.Sum(expense => expense.Amount)))
                .OrderByDescending(c => c.Total)
                .ToArray());
    }

    public async Task<IReadOnlyList<RestaurantWaiterSales>> GetWaiterSalesAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        var (startUtc, endUtc) = ToUtcRange(from, to);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(e.Name, 'Não informado'), p.OrderId, p.PaidAt, p.Amount
            FROM RestaurantPayments p
            JOIN RestaurantOrders o ON o.Id = p.OrderId
            LEFT JOIN RestaurantEmployees e ON e.Id = o.EmployeeId;
            """;
        var rows = new List<(string Name, string OrderId, decimal Amount)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var paidAt = DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture);
                if (paidAt >= startUtc && paidAt < endUtc)
                    rows.Add((reader.GetString(0), reader.GetString(1), decimal.Parse(reader.GetString(3), CultureInfo.InvariantCulture)));
            }
        }
        return rows
            .GroupBy(row => row.Name)
            .Select(g => new RestaurantWaiterSales(g.Key, g.Select(r => r.OrderId).Distinct().Count(), g.Sum(r => r.Amount)))
            .OrderByDescending(sales => sales.Total)
            .ToArray();
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
        await using (var reset = connection.CreateCommand())
        {
            reset.Transaction = (SqliteTransaction)transaction;
            reset.CommandText = "DELETE FROM AppSettings WHERE Key = 'printBatch.counter';";
            await reset.ExecuteNonQueryAsync(cancellationToken);
        }
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
