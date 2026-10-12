using Microsoft.Data.Sqlite;

namespace SYSMR.Infrastructure.Persistence;

public sealed class SqliteDatabase(string databasePath)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        ForeignKeys = true
    }.ToString();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Products (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                Barcode TEXT NOT NULL DEFAULT '',
                ScaleCode TEXT NOT NULL DEFAULT '',
                CostPrice TEXT NOT NULL,
                SalePrice TEXT NOT NULL,
                Stock TEXT NOT NULL,
                MinimumStock TEXT NOT NULL,
                Category TEXT NOT NULL DEFAULT '',
                Ncm TEXT NOT NULL DEFAULT '',
                Cest TEXT NOT NULL DEFAULT '',
                SoldByWeight INTEGER NOT NULL DEFAULT 0,
                UnitOfMeasure TEXT NOT NULL DEFAULT 'UN'
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_Products_Barcode
                ON Products(Barcode) WHERE Barcode <> '';
            CREATE UNIQUE INDEX IF NOT EXISTS IX_Products_ScaleCode
                ON Products(ScaleCode) WHERE ScaleCode <> '';
            CREATE TABLE IF NOT EXISTS Sales (
                Id TEXT PRIMARY KEY,
                CreatedAt TEXT NOT NULL,
                PaymentMethod TEXT NOT NULL,
                Total TEXT NOT NULL,
                CustomerDocument TEXT,
                SellerName TEXT NOT NULL DEFAULT 'Não informado',
                SyncStatus INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS Customers (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                Document TEXT NOT NULL DEFAULT '',
                Phone TEXT NOT NULL DEFAULT '',
                Email TEXT NOT NULL DEFAULT '',
                RegisteredAt TEXT NOT NULL,
                ExpiresAt TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS Sellers (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                IsActive INTEGER NOT NULL DEFAULT 1
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_Customers_Document
                ON Customers(Document) WHERE Document <> '';
            CREATE TABLE IF NOT EXISTS SaleItems (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SaleId TEXT NOT NULL REFERENCES Sales(Id),
                ProductId TEXT NOT NULL,
                ProductName TEXT NOT NULL,
                Quantity TEXT NOT NULL,
                UnitPrice TEXT NOT NULL,
                Total TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS Returns (
                Id TEXT PRIMARY KEY,
                SaleId TEXT NOT NULL UNIQUE REFERENCES Sales(Id),
                CreatedAt TEXT NOT NULL,
                Amount TEXT NOT NULL,
                PaymentMethod TEXT NOT NULL,
                SyncStatus INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS ReturnItems (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ReturnId TEXT NOT NULL REFERENCES Returns(Id),
                ProductId TEXT NOT NULL,
                ProductName TEXT NOT NULL,
                Quantity TEXT NOT NULL,
                UnitPrice TEXT NOT NULL,
                Total TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS CashMovements (
                Id TEXT PRIMARY KEY,
                CreatedAt TEXT NOT NULL,
                Type TEXT NOT NULL,
                PaymentMethod TEXT NOT NULL,
                Amount TEXT NOT NULL,
                ReferenceId TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS AppSettings (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS StoreAccountCache (
                Id INTEGER PRIMARY KEY CHECK (Id = 1),
                CustomerNumber TEXT NOT NULL,
                CustomerName TEXT NOT NULL,
                FirebaseUid TEXT NOT NULL,
                PasswordSalt TEXT NOT NULL,
                PasswordHash TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                AutoLogin INTEGER NOT NULL DEFAULT 0,
                StoreType TEXT NOT NULL DEFAULT 'sales',
                ProtectedRefreshToken TEXT
            );
            CREATE TABLE IF NOT EXISTS SyncOutbox (
                Id TEXT PRIMARY KEY,
                Branch TEXT NOT NULL,
                Entity TEXT NOT NULL,
                EntityId TEXT NOT NULL,
                Payload TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                SyncedAt TEXT,
                Attempts INTEGER NOT NULL DEFAULT 0,
                LastError TEXT
            );
            CREATE INDEX IF NOT EXISTS IX_SyncOutbox_Pending
                ON SyncOutbox(SyncedAt, CreatedAt);
            CREATE TABLE IF NOT EXISTS AuditLog (
                Id TEXT PRIMARY KEY,
                CreatedAt TEXT NOT NULL,
                Actor TEXT NOT NULL,
                Action TEXT NOT NULL,
                Details TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS RestaurantAreas (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                SortOrder INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS RestaurantTables (
                Id TEXT PRIMARY KEY,
                AreaId TEXT NOT NULL REFERENCES RestaurantAreas(Id),
                Number INTEGER NOT NULL,
                IsActive INTEGER NOT NULL DEFAULT 1,
                UNIQUE (AreaId, Number)
            );
            CREATE TABLE IF NOT EXISTS RestaurantMenuItems (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                Category TEXT NOT NULL DEFAULT '',
                Price TEXT NOT NULL,
                IsActive INTEGER NOT NULL DEFAULT 1,
                InventoryProductId TEXT REFERENCES Products(Id),
                InventoryQuantityPerUnit TEXT NOT NULL DEFAULT '0'
            );
            CREATE TABLE IF NOT EXISTS RestaurantEmployees (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                Role TEXT NOT NULL DEFAULT 'Atendente',
                IsActive INTEGER NOT NULL DEFAULT 1
            );
            CREATE TABLE IF NOT EXISTS RestaurantOrders (
                Id TEXT PRIMARY KEY,
                TableId TEXT NOT NULL REFERENCES RestaurantTables(Id),
                EmployeeId TEXT REFERENCES RestaurantEmployees(Id),
                OpenedAt TEXT NOT NULL,
                ClosedAt TEXT,
                Status TEXT NOT NULL DEFAULT 'Open'
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_RestaurantOrders_OpenTable
                ON RestaurantOrders(TableId) WHERE Status = 'Open';
            CREATE TABLE IF NOT EXISTS RestaurantOrderItems (
                Id TEXT PRIMARY KEY,
                OrderId TEXT NOT NULL REFERENCES RestaurantOrders(Id),
                MenuItemId TEXT REFERENCES RestaurantMenuItems(Id),
                MenuItemName TEXT NOT NULL,
                Quantity TEXT NOT NULL,
                UnitPrice TEXT NOT NULL,
                InventoryProductId TEXT REFERENCES Products(Id),
                InventoryQuantityPerUnit TEXT NOT NULL DEFAULT '0'
            );
            CREATE TABLE IF NOT EXISTS RestaurantPayments (
                Id TEXT PRIMARY KEY,
                OrderId TEXT NOT NULL REFERENCES RestaurantOrders(Id),
                SessionId TEXT NOT NULL REFERENCES RestaurantCashSessions(Id),
                PaidAt TEXT NOT NULL,
                PaymentMethod TEXT NOT NULL,
                Amount TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS RestaurantCashSessions (
                Id TEXT PRIMARY KEY,
                OpenedAt TEXT NOT NULL,
                OpeningAmount TEXT NOT NULL,
                ClosedAt TEXT,
                ClosingAmount TEXT
            );
            CREATE TABLE IF NOT EXISTS RestaurantCashMovements (
                Id TEXT PRIMARY KEY,
                SessionId TEXT NOT NULL REFERENCES RestaurantCashSessions(Id),
                CreatedAt TEXT NOT NULL,
                Type TEXT NOT NULL,
                Amount TEXT NOT NULL,
                Note TEXT NOT NULL DEFAULT ''
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureColumnAsync(connection, "Products", "UnitOfMeasure", "TEXT NOT NULL DEFAULT 'UN'");
        await using (var migrateWeightedUnits = connection.CreateCommand())
        {
            migrateWeightedUnits.CommandText = "UPDATE Products SET UnitOfMeasure = 'KG' WHERE SoldByWeight = 1 AND UnitOfMeasure = 'UN';";
            await migrateWeightedUnits.ExecuteNonQueryAsync(cancellationToken);
        }
        await EnsureColumnAsync(connection, "Sales", "CustomerId", "TEXT");
        await EnsureColumnAsync(connection, "Sales", "SellerName", "TEXT NOT NULL DEFAULT 'Não informado'");
        await EnsureColumnAsync(connection, "StoreAccountCache", "AutoLogin", "INTEGER NOT NULL DEFAULT 1");
        await EnsureColumnAsync(connection, "StoreAccountCache", "StoreType", "TEXT NOT NULL DEFAULT 'sales'");
        await EnsureColumnAsync(connection, "StoreAccountCache", "ProtectedRefreshToken", "TEXT");
        await EnsureColumnAsync(connection, "RestaurantTables", "IsActive", "INTEGER NOT NULL DEFAULT 1");
        await EnsureColumnAsync(connection, "RestaurantOrders", "WaiterServiceChargeEnabled", "INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(connection, "RestaurantMenuItems", "InventoryProductId", "TEXT REFERENCES Products(Id)");
        await EnsureColumnAsync(connection, "RestaurantMenuItems", "InventoryQuantityPerUnit", "TEXT NOT NULL DEFAULT '0'");
        await EnsureColumnAsync(connection, "RestaurantOrderItems", "InventoryProductId", "TEXT REFERENCES Products(Id)");
        await EnsureColumnAsync(connection, "RestaurantOrderItems", "InventoryQuantityPerUnit", "TEXT NOT NULL DEFAULT '0'");
        await MigrateRestaurantPaymentsAsync(connection, cancellationToken);
        await SeedProductsAsync(connection, cancellationToken);
    }

    public SqliteConnection CreateConnection() => new(_connectionString);

    private static async Task EnsureColumnAsync(SqliteConnection connection, string table, string column, string declaration)
    {
        await using var inspect = connection.CreateCommand();
        inspect.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await inspect.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase))
                return;
        }
        await reader.DisposeAsync();
        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {declaration};";
        await alter.ExecuteNonQueryAsync();
    }

    private static async Task MigrateRestaurantPaymentsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var inspect = connection.CreateCommand();
        inspect.CommandText = """
            SELECT sql FROM sqlite_master
            WHERE type = 'table' AND name = 'RestaurantPayments';
            """;
        var schema = (string?)await inspect.ExecuteScalarAsync(cancellationToken);
        if (schema is null || !schema.Contains("OrderId TEXT NOT NULL UNIQUE", StringComparison.OrdinalIgnoreCase))
            return;

        await using var migrate = connection.CreateCommand();
        migrate.CommandText = """
            CREATE TABLE RestaurantPayments_New (
                Id TEXT PRIMARY KEY,
                OrderId TEXT NOT NULL REFERENCES RestaurantOrders(Id),
                SessionId TEXT NOT NULL REFERENCES RestaurantCashSessions(Id),
                PaidAt TEXT NOT NULL,
                PaymentMethod TEXT NOT NULL,
                Amount TEXT NOT NULL
            );
            INSERT INTO RestaurantPayments_New (Id, OrderId, SessionId, PaidAt, PaymentMethod, Amount)
            SELECT Id, OrderId, SessionId, PaidAt, PaymentMethod, Amount FROM RestaurantPayments;
            DROP TABLE RestaurantPayments;
            ALTER TABLE RestaurantPayments_New RENAME TO RestaurantPayments;
            """;
        await migrate.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SeedProductsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO Products
                (Id, Name, Barcode, ScaleCode, CostPrice, SalePrice, Stock, MinimumStock, Category, SoldByWeight, UnitOfMeasure)
            VALUES
                ($milkId, 'Leite integral 1L', '7891000100103', '', '3.50', '5.49', '24', '5', 'Mercearia', 0, 'UN'),
                ($coffeeId, 'Cafe torrado 500g', '7891000315507', '', '12.00', '18.90', '12', '3', 'Mercearia', 0, 'UN'),
                ($appleId, 'Maca Fuji (kg)', '', '01234', '4.00', '8.99', '30', '5', 'Hortifruti', 1, 'KG');
            """;
        command.Parameters.AddWithValue("$milkId", Guid.Parse("ed27545d-773d-4bf5-92e0-5e6619446010").ToString());
        command.Parameters.AddWithValue("$coffeeId", Guid.Parse("39ad10ee-f40c-4423-b114-f6eb60f3d7df").ToString());
        command.Parameters.AddWithValue("$appleId", Guid.Parse("8a604276-54a9-43f6-bcb8-9050bd42c7cb").ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
