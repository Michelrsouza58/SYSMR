using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SYSMR.Application.Services;
using SYSMR.Application.Models;
using SYSMR.Domain.Entities;
using SYSMR.Infrastructure.LocalServer;
using SYSMR.Infrastructure.Persistence;
using SYSMR.Infrastructure.Printing;

namespace SYSMR.Tests;

public sealed class OfflineWorkflowTests : IAsyncLifetime
{
    private readonly string _testDirectory = Path.Combine(Path.GetTempPath(), "SYSMR.Tests", Guid.NewGuid().ToString("N"));
    private SqliteDatabase _database = null!;
    private SqliteProductRepository _products = null!;
    private SqliteSaleRepository _sales = null!;

    public async Task InitializeAsync()
    {
        _database = new SqliteDatabase(Path.Combine(_testDirectory, "sysmr.db"));
        await _database.InitializeAsync();
        _products = new SqliteProductRepository(_database);
        _sales = new SqliteSaleRepository(_database);
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testDirectory))
            Directory.Delete(_testDirectory, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CatalogFindsExactBarcodeAndParsesScaleWeight()
    {
        var catalog = new ProductCatalogService(_products);

        var exact = await catalog.FindScannedProductAsync("7891000100103");
        var scaleLabel = CreateScaleLabel("01234", 125);
        var weighed = await catalog.FindScannedProductAsync(scaleLabel);
        var invalidScaleLabel = scaleLabel[..^1] + ((scaleLabel[^1] - '0' + 1) % 10);

        Assert.NotNull(exact);
        Assert.Equal("Leite integral 1L", exact.Product.Name);
        Assert.Equal(1m, exact.Quantity);
        Assert.NotNull(weighed);
        Assert.Equal("Maca Fuji (kg)", weighed.Product.Name);
        Assert.Equal(0.125m, weighed.Quantity);
        Assert.Null(await catalog.FindScannedProductAsync(invalidScaleLabel));
    }

    [Fact]
    public async Task SearchAndSalePersistenceUseLocalDatabase()
    {
        var matches = await _products.SearchAsync("Cafe");
        Assert.Single(matches);
        Assert.Equal("Cafe torrado 500g", matches[0].Name);

        var milk = await _products.FindByBarcodeAsync("7891000100103");
        Assert.NotNull(milk);
        var service = new SalesService(_sales);
        await service.CompleteAsync(
            [new SaleItem(milk.Id, milk.Name, 2m, milk.SalePrice)],
            PaymentMethod.Pix,
            null,
            sellerName: "Vendedor A");

        Assert.Equal(10.98m, await service.GetSalesTotalAsync());
        Assert.Equal(1, await service.GetSalesCountAsync());
        Assert.Equal(22m, (await _products.FindByBarcodeAsync(milk.Barcode))!.Stock);
        var outbox = new SqliteSyncOutboxRepository(_database);
        var pendingSync = Assert.Single((await outbox.GetPendingAsync()).Where(item => item.Entity == "sale"));
        Assert.Equal("sales", pendingSync.Branch);
        await outbox.MarkSyncedAsync(pendingSync);
        Assert.Single(await outbox.GetPendingAsync());
        await using var syncConnection = _database.CreateConnection();
        await syncConnection.OpenAsync();
        await using var syncStatus = syncConnection.CreateCommand();
        syncStatus.CommandText = "SELECT SyncStatus FROM Sales LIMIT 1;";
        Assert.Equal(1L, await syncStatus.ExecuteScalarAsync());
        var history = Assert.Single(await service.GetSalesAsync("Vendedor A"));
        Assert.Equal("Vendedor A", history.SellerName);
        var audit = Assert.Single(await new SqliteAuditLogRepository(_database).SearchAsync("Venda concluída"));
        Assert.Equal("Vendedor A", audit.Actor);
    }

    [Fact]
    public async Task CustomerAccountSupportsPartialAndFinalPaymentsWithoutPrematureCashIncome()
    {
        var milk = await _products.FindByBarcodeAsync("7891000100103");
        Assert.NotNull(milk);
        var customer = new Customer(
            Guid.NewGuid(), "Cliente de teste", "", "", "",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30));
        await new SqliteCustomerRepository(_database).SaveAsync(customer);
        var service = new SalesService(_sales);
        var saleId = await service.CompleteAsync(
            [new SaleItem(milk.Id, milk.Name, 2m, milk.SalePrice)],
            PaymentMethod.StoreCredit,
            customer.Document,
            customer.Id,
            "Vendedor A");

        var openAccount = Assert.Single(await service.GetOpenAccountsAsync(customer.Id));
        Assert.Equal(10.98m, openAccount.BalanceDue);
        Assert.Single(await service.GetSalesAsync("Vendedor A", customerId: customer.Id));
        await using (var connection = _database.CreateConnection())
        {
            await connection.OpenAsync();
            await using var cashMovementCount = connection.CreateCommand();
            cashMovementCount.CommandText = "SELECT COUNT(*) FROM CashMovements;";
            Assert.Equal(0L, await cashMovementCount.ExecuteScalarAsync());
        }

        await service.RegisterAccountPaymentAsync(saleId, 3.50m, PaymentMethod.Pix, "Operador");
        var partialBalance = Assert.Single(await service.GetOpenAccountsAsync(customer.Id));
        Assert.Equal(7.48m, partialBalance.BalanceDue);
        var partialRecord = await service.GetSaleAsync(saleId);
        Assert.NotNull(partialRecord);
        Assert.Equal(3.50m, partialRecord.Summary.AmountPaid);
        Assert.Equal(7.48m, partialRecord.Summary.BalanceDue);
        Assert.Single(partialRecord.Payments);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RegisterAccountPaymentAsync(saleId, 7.49m, PaymentMethod.Cash, "Operador"));
        Assert.Equal(7.48m, Assert.Single(await service.GetOpenAccountsAsync(customer.Id)).BalanceDue);

        await service.RegisterAccountPaymentAsync(saleId, 7.48m, PaymentMethod.Cash, "Operador");
        Assert.Empty(await service.GetOpenAccountsAsync(customer.Id));
        var paidRecord = await service.GetSaleAsync(saleId);
        Assert.NotNull(paidRecord);
        Assert.Equal(10.98m, paidRecord.Summary.AmountPaid);
        Assert.Equal(0m, paidRecord.Summary.BalanceDue);
        Assert.Equal(2, paidRecord.Payments.Count);
        Assert.Equal(2, (await new SqliteSyncOutboxRepository(_database).GetPendingAsync())
            .Count(item => item.Entity == "account-payment"));

        await using var ledgerConnection = _database.CreateConnection();
        await ledgerConnection.OpenAsync();
        await using var ledger = ledgerConnection.CreateCommand();
        ledger.CommandText = "SELECT Amount FROM CashMovements ORDER BY rowid;";
        await using var ledgerReader = await ledger.ExecuteReaderAsync();
        Assert.True(await ledgerReader.ReadAsync());
        Assert.Equal("3.50", ledgerReader.GetString(0));
        Assert.True(await ledgerReader.ReadAsync());
        Assert.Equal("7.48", ledgerReader.GetString(0));
        Assert.False(await ledgerReader.ReadAsync());
    }

    [Fact]
    public async Task ReturnOnCustomerAccountReducesDebtBeforeRefundingCash()
    {
        var milk = await _products.FindByBarcodeAsync("7891000100103");
        Assert.NotNull(milk);
        var customer = new Customer(
            Guid.NewGuid(), "Cliente devolução", "", "", "",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30));
        await new SqliteCustomerRepository(_database).SaveAsync(customer);
        var service = new SalesService(_sales);
        var saleId = await service.CompleteAsync(
            [new SaleItem(milk.Id, milk.Name, 2m, milk.SalePrice)],
            PaymentMethod.StoreCredit,
            null,
            customerId: customer.Id);

        var sale = await service.GetSaleAsync(saleId);
        Assert.NotNull(sale);
        await service.ReturnSaleAsync(saleId, [sale.Items[0] with { Quantity = 1m }]);
        var openNote = Assert.Single(await service.GetOpenAccountsAsync(customer.Id));
        Assert.Equal(5.49m, openNote.BalanceDue);

        await service.RegisterAccountPaymentAsync(saleId, 2m, PaymentMethod.Cash, "Operador");
        await service.ReturnSaleAsync(saleId, [sale.Items[0] with { Quantity = 1m }]);
        Assert.Empty(await service.GetOpenAccountsAsync(customer.Id));

        await using var connection = _database.CreateConnection();
        await connection.OpenAsync();
        await using var movementCommand = connection.CreateCommand();
        movementCommand.CommandText = "SELECT Type, Amount FROM CashMovements ORDER BY rowid;";
        await using var movementReader = await movementCommand.ExecuteReaderAsync();
        Assert.True(await movementReader.ReadAsync());
        Assert.Equal("AccountPayment", movementReader.GetString(0));
        Assert.Equal("2", movementReader.GetString(1));
        Assert.True(await movementReader.ReadAsync());
        Assert.Equal("Refund", movementReader.GetString(0));
        Assert.Equal("-2.00", movementReader.GetString(1));
    }

    [Fact]
    public async Task SellersAreSavedAndOnlyActiveSellersAreAvailableForSales()
    {
        var sellers = new SqliteSellerRepository(_database);
        var activeSeller = new SellerRecord(Guid.NewGuid(), "Ana Silva", true);
        var inactiveSeller = new SellerRecord(Guid.NewGuid(), "Bruno Costa", false);

        await sellers.SaveAsync(activeSeller);
        await sellers.SaveAsync(inactiveSeller);

        Assert.Equal([activeSeller], await sellers.GetAllAsync(activeOnly: true));
        Assert.Equal(2, (await sellers.GetAllAsync()).Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sellers.SaveAsync(new SellerRecord(Guid.NewGuid(), "ana silva", true)));

        await sellers.SaveAsync(activeSeller with { IsActive = false });
        Assert.Empty(await sellers.GetAllAsync(activeOnly: true));
    }

    [Fact]
    public async Task InsufficientStockRollsBackWholeSaleAndStockChanges()
    {
        var milk = await _products.FindByBarcodeAsync("7891000100103");
        var coffee = await _products.FindByBarcodeAsync("7891000315507");
        Assert.NotNull(milk);
        Assert.NotNull(coffee);

        var sale = new SalesService(_sales);
        await Assert.ThrowsAsync<InvalidOperationException>(() => sale.CompleteAsync(
        [
            new SaleItem(milk.Id, milk.Name, 1m, milk.SalePrice),
            new SaleItem(coffee.Id, coffee.Name, 20m, coffee.SalePrice)
        ], PaymentMethod.Cash, null));

        Assert.Equal(0, await sale.GetSalesCountAsync());
        Assert.Equal(24m, (await _products.FindByBarcodeAsync(milk.Barcode))!.Stock);
        Assert.Equal(12m, (await _products.FindByBarcodeAsync(coffee.Barcode))!.Stock);
    }

    [Fact]
    public async Task FullReturnRestocksAndOffsetsSaleInCashLedger()
    {
        var milk = await _products.FindByBarcodeAsync("7891000100103");
        Assert.NotNull(milk);
        var service = new SalesService(_sales);
        await service.CompleteAsync(
            [new SaleItem(milk.Id, milk.Name, 2m, milk.SalePrice)],
            PaymentMethod.Pix,
            null);

        var sale = Assert.Single(await service.GetSalesAsync(string.Empty));
        var saleRecord = await service.GetSaleAsync(sale.Id);
        Assert.NotNull(saleRecord);
        await service.ReturnSaleAsync(sale.Id, saleRecord.Items);

        Assert.Equal(0m, await service.GetSalesTotalAsync());
        Assert.Equal(24m, (await _products.FindByBarcodeAsync(milk.Barcode))!.Stock);
        var returnedSale = await service.GetSaleAsync(sale.Id);
        Assert.NotNull(returnedSale);
        Assert.True(returnedSale.Summary.IsReturned);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReturnSaleAsync(sale.Id, saleRecord.Items));
        var pendingReturn = Assert.Single((await new SqliteSyncOutboxRepository(_database).GetPendingAsync())
            .Where(item => item.Entity == "return"));
        await new SqliteSyncOutboxRepository(_database).MarkSyncedAsync(pendingReturn);
        await using (var returnConnection = _database.CreateConnection())
        {
            await returnConnection.OpenAsync();
            await using var returnStatus = returnConnection.CreateCommand();
            returnStatus.CommandText = "SELECT SyncStatus FROM Returns WHERE Id = $id;";
            returnStatus.Parameters.AddWithValue("$id", pendingReturn.EntityId);
            Assert.Equal(1L, await returnStatus.ExecuteScalarAsync());
        }
        var returnAudit = Assert.Single(await new SqliteAuditLogRepository(_database).SearchAsync("Devolução integral"));
        Assert.Contains("Super Admin", returnAudit.Actor);

        await using var connection = _database.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Amount FROM CashMovements ORDER BY CreatedAt;";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("10.98", reader.GetString(0));
        Assert.True(await reader.ReadAsync());
        Assert.Equal("-10.98", reader.GetString(0));
    }

    [Fact]
    public async Task PartialReturnsRestoreSelectedQuantitiesAndCanBeCompletedLater()
    {
        var milk = await _products.FindByBarcodeAsync("7891000100103");
        Assert.NotNull(milk);
        var service = new SalesService(_sales);
        await service.CompleteAsync(
            [new SaleItem(milk.Id, milk.Name, 3m, milk.SalePrice)],
            PaymentMethod.Cash,
            null,
            sellerName: "Vendedora");
        var sale = Assert.Single(await service.GetSalesAsync(string.Empty));

        await service.ReturnSaleAsync(
            sale.Id,
            [new SaleItem(milk.Id, milk.Name, 1m, milk.SalePrice)],
            actor: "Loja");

        Assert.Equal(22m, (await _products.FindByBarcodeAsync(milk.Barcode))!.Stock);
        Assert.Equal(10.98m, await service.GetSalesTotalAsync());
        Assert.False(Assert.Single(await service.GetSalesAsync(string.Empty)).IsReturned);
        var partlyReturned = await service.GetSaleAsync(sale.Id);
        Assert.NotNull(partlyReturned);
        Assert.Equal(1m, partlyReturned.ReturnedQuantities[milk.Id]);

        await service.ReturnSaleAsync(
            sale.Id,
            [new SaleItem(milk.Id, milk.Name, 2m, milk.SalePrice)],
            actor: "Loja");

        Assert.Equal(24m, (await _products.FindByBarcodeAsync(milk.Barcode))!.Stock);
        Assert.Equal(0m, await service.GetSalesTotalAsync());
        Assert.True(Assert.Single(await service.GetSalesAsync(string.Empty)).IsReturned);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReturnSaleAsync(
            sale.Id,
            [new SaleItem(milk.Id, milk.Name, 1m, milk.SalePrice)]));
        Assert.Equal(24m, (await _products.FindByBarcodeAsync(milk.Barcode))!.Stock);

        await using var connection = _database.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM CashMovements WHERE Type = 'Refund';";
        Assert.Equal(2L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task CustomerRegistrationCanBeRenewedWithoutDisablingUse()
    {
        var repository = new SqliteCustomerRepository(_database);
        var registeredAt = DateTimeOffset.UtcNow.AddDays(-40);
        var customer = new Customer(
            Guid.NewGuid(),
            "Cliente de teste",
            "12345678900",
            "11999990000",
            "cliente@example.test",
            registeredAt,
            registeredAt.AddDays(30));
        await repository.SaveAsync(customer);

        var expired = Assert.Single(await repository.SearchAsync("Cliente de teste"));
        Assert.True(expired.NeedsReview);
        Assert.Equal("REVISAR", expired.StatusLabel);

        var renewedAt = DateTimeOffset.UtcNow;
        await repository.RenewAsync(customer.Id, renewedAt);
        var renewed = Assert.Single(await repository.SearchAsync(customer.Document));
        Assert.False(renewed.NeedsReview);
        Assert.Equal(renewedAt.AddDays(30).Date, renewed.ExpiresAt.Date);
    }

    [Fact]
    public async Task SuperAdminPinIsStoredAsHashAndCanBeVerified()
    {
        var accessControl = new SqliteAccessControl(_database);
        Assert.False(await accessControl.IsAdministratorConfiguredAsync());

        await accessControl.ConfigureAdministratorAsync("LocalOwner!2026");

        Assert.True(await accessControl.IsAdministratorConfiguredAsync());
        Assert.True(await accessControl.VerifyAdministratorPinAsync("LocalOwner!2026"));
        Assert.False(await accessControl.VerifyAdministratorPinAsync("WrongPin!2026"));
        Assert.NotEqual("LocalOwner!2026", await accessControl.GetSettingAsync("superadmin.hash"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            accessControl.ConfigureAdministratorAsync("AnotherPin!2026"));
    }

    [Fact]
    public async Task StoreAccountUsesOnlineCredentialCacheForOfflineLoginWithoutStoringPassword()
    {
        var accounts = new SqliteStoreAccountRepository(_database);
        const string password = "LojaSegura!2026";
        await accounts.SaveAuthenticatedAccountAsync("001-ABC", "Mercado Central", password, "firebase-user-1", "restaurant");

        var account = await accounts.VerifyOfflineAsync("001-abc", "Mercado Central", password);
        Assert.NotNull(account);
        Assert.Equal("001-ABC", account.CustomerNumber);
        Assert.Equal("firebase-user-1", account.FirebaseUid);
        Assert.Equal("restaurant", account.StoreType);
        Assert.Equal(account, await accounts.GetRememberedAccountAsync());
        await accounts.UpdateStoreTypeAndRefreshTokenAsync("sales", "rotated-refresh-token");
        account = await accounts.GetRememberedAccountAsync();
        Assert.NotNull(account);
        Assert.Equal("sales", account.StoreType);
        Assert.Equal("rotated-refresh-token", await accounts.GetRefreshTokenAsync());
        Assert.Null(await accounts.VerifyOfflineAsync("001-ABC", "Outra loja", password));
        Assert.Null(await accounts.VerifyOfflineAsync("001-ABC", "Mercado Central", "SenhaErrada!"));

        await using (var connection = _database.CreateConnection())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT PasswordSalt, PasswordHash FROM StoreAccountCache WHERE Id = 1;";
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.NotEqual(password, reader.GetString(0));
            Assert.NotEqual(password, reader.GetString(1));
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            accounts.SaveAuthenticatedAccountAsync("002-ABC", "Outra loja", password, "firebase-user-2"));

        await accounts.DisableAutoLoginAsync();
        Assert.Null(await accounts.GetRememberedAccountAsync());
        await accounts.EnableAutoLoginAsync();
        Assert.Equal(account, await accounts.GetRememberedAccountAsync());

        Assert.Equal(
            FirebaseStoreAccountClient.CreateLoginEmail("001-ABC"),
            FirebaseStoreAccountClient.CreateLoginEmail("001-abc"));
        Assert.DoesNotContain("001-ABC", FirebaseStoreAccountClient.CreateLoginEmail("001-ABC"));
        Assert.Equal("001-abc", FirebaseStoreAccountClient.CreateWaiterStoreSlug("001-ABC"));
        Assert.Equal(
            "garcom.001-abc.0042@login.sysmr.invalid",
            FirebaseStoreAccountClient.CreateWaiterLoginEmail("001-abc", "0042"));
        Assert.NotEqual(
            FirebaseStoreAccountClient.CreateWaiterLoginEmail("001-abc", "0042"),
            FirebaseStoreAccountClient.CreateWaiterLoginEmail("001-abc", "42"));
        Assert.Throws<ArgumentException>(() => FirebaseStoreAccountClient.CreateWaiterStoreSlug("-01"));
        Assert.Throws<ArgumentException>(() => FirebaseStoreAccountClient.CreateWaiterStoreSlug("01-"));
    }

    [Fact]
    public async Task UnlinkStoreWithClearRemovesDependentDataAndPreservesComputerConfiguration()
    {
        var accounts = new SqliteStoreAccountRepository(_database);
        await accounts.SaveAuthenticatedAccountAsync("TEST-01", "Loja teste", "senha-segura", "store-uid", "restaurant");
        await using (var connection = _database.CreateConnection())
        {
            await connection.OpenAsync();
            await using var populate = connection.CreateCommand();
            populate.CommandText = """
                INSERT INTO AppSettings (Key, Value) VALUES
                    ('firebase.projectId', 'project'), ('printer.kitchen', 'queue'),
                    ('superadmin.hash', 'hash'), ('receipt.storeName', 'Loja antiga'),
                    ('receipt.phone', '111'), ('receipt.address', 'Rua antiga'),
                    ('receipt.footer', 'Antigo'), ('waiter.commissionPercent', '5'),
                    ('printBatch.counter', '12'), ('restaurant.tables.defaultCount', '12');
                INSERT INTO Customers VALUES ('customer', 'Cliente', '', '', '', '2026', '2027');
                INSERT INTO Sellers VALUES ('seller', 'Vendedor', 1);
                INSERT INTO Sales (Id, CreatedAt, PaymentMethod, Total) VALUES ('sale', '2026', 'Cash', '10');
                INSERT INTO SaleItems (SaleId, ProductId, ProductName, Quantity, UnitPrice, Total)
                    VALUES ('sale', 'product', 'Produto', '1', '10', '10');
                INSERT INTO SalePayments VALUES ('sp', 'sale', '2026', 'Cash', '10');
                INSERT INTO Returns VALUES ('return', 'sale', '2026', '10', 'Cash', 0);
                INSERT INTO ReturnItems (ReturnId, ProductId, ProductName, Quantity, UnitPrice, Total)
                    VALUES ('return', 'product', 'Produto', '1', '10', '10');
                INSERT INTO CashMovements VALUES ('cm', '2026', 'Sale', 'Cash', '10', 'sale');
                INSERT INTO RestaurantAreas (Id, Name) VALUES ('area', 'Área');
                INSERT INTO RestaurantTables (Id, AreaId, Number) VALUES ('table', 'area', 1);
                INSERT INTO RestaurantEmployees (Id, Name) VALUES ('employee', 'Garçom');
                INSERT INTO RestaurantMenuItems (Id, Name, Price, InventoryProductId)
                    SELECT 'menu', 'Item', '10', Id FROM Products LIMIT 1;
                INSERT INTO RestaurantMenuItemOptions (Id, MenuItemId, Name) VALUES ('option', 'menu', 'Opção');
                INSERT INTO RestaurantOrders (Id, TableId, EmployeeId, OpenedAt) VALUES ('order', 'table', 'employee', '2026');
                INSERT INTO RestaurantOrderItems (Id, OrderId, MenuItemId, MenuItemName, Quantity, UnitPrice, InventoryProductId, SendingEmployeeId)
                    SELECT 'item', 'order', 'menu', 'Item', '2', '10', Id, 'employee' FROM Products LIMIT 1;
                INSERT INTO RestaurantCashSessions (Id, OpenedAt, OpeningAmount) VALUES ('session', '2026', '0');
                INSERT INTO RestaurantPayments VALUES ('payment', 'order', 'session', '2026', 'Cash', '10');
                INSERT INTO RestaurantPaymentItems VALUES ('payment', 'item', '1');
                INSERT INTO RestaurantExpenses VALUES ('expense', '2026', 'Geral', 'Gasto', '1', 1, 'session');
                INSERT INTO RestaurantCashMovements VALUES ('rcm', 'session', '2026', 'Supply', '1', '');
                INSERT INTO RestaurantWaiterCommands VALUES ('command', 'applied', NULL, 'order', '2026');
                INSERT INTO RestaurantItemChangeNotices VALUES ('notice', 'order', 1, 'Área', 'Item', 'kitchen', NULL, 'remove', '', '', '2026', 'pending', NULL);
                INSERT INTO SyncOutbox (Id, Branch, Entity, EntityId, Payload, CreatedAt)
                    VALUES ('outbox', 'restaurant', 'item', 'item', '{}', '2026');
                INSERT INTO AuditLog VALUES ('audit', '2026', 'Operador', 'Venda', 'Antiga');
                """;
            await populate.ExecuteNonQueryAsync();
        }

        await accounts.RemoveAccountAsync(clearLocalData: true);
        Assert.False(await accounts.HasAccountAsync());
        await _database.InitializeAsync();
        await using var verify = _database.CreateConnection();
        await verify.OpenAsync();
        var tables = new[]
        {
            "Products", "Sales", "SaleItems", "SalePayments", "Returns", "ReturnItems", "CashMovements",
            "Customers", "Sellers", "RestaurantAreas", "RestaurantTables", "RestaurantEmployees",
            "RestaurantMenuItems", "RestaurantMenuItemOptions", "RestaurantOrders", "RestaurantOrderItems",
            "RestaurantPayments", "RestaurantPaymentItems", "RestaurantCashSessions", "RestaurantExpenses",
            "RestaurantCashMovements", "RestaurantWaiterCommands", "RestaurantItemChangeNotices", "SyncOutbox"
        };
        foreach (var table in tables)
        {
            await using var count = verify.CreateCommand();
            count.CommandText = $"SELECT COUNT(*) FROM {table};";
            Assert.Equal(0L, await count.ExecuteScalarAsync());
        }
        await using var settings = verify.CreateCommand();
        settings.CommandText = """
            SELECT COUNT(*) FROM AppSettings WHERE Key IN
            ('receipt.storeName', 'receipt.phone', 'receipt.address', 'receipt.footer',
             'waiter.commissionPercent', 'printBatch.counter', 'restaurant.tables.defaultCount');
            """;
        Assert.Equal(0L, await settings.ExecuteScalarAsync());
        var access = new SqliteAccessControl(_database);
        Assert.Equal("project", await access.GetSettingAsync("firebase.projectId"));
        Assert.Equal("queue", await access.GetSettingAsync("printer.kitchen"));
        Assert.Equal("hash", await access.GetSettingAsync("superadmin.hash"));
        var audit = Assert.Single(await new SqliteAuditLogRepository(_database).SearchAsync(""));
        Assert.Equal("Loja desvinculada", audit.Action);
        await accounts.SaveAuthenticatedAccountAsync("TEST-02", "Nova loja", "senha-segura", "new-store-uid", "restaurant");
        var restaurant = new SqliteRestaurantRepository(_database);
        await restaurant.InitializeAsync();
        Assert.Empty(await restaurant.GetEmployeesAsync());
        Assert.Single(await restaurant.GetAreasAsync());
        Assert.Equal("new-store-uid", (await accounts.GetRememberedAccountAsync())!.FirebaseUid);
    }

    [Fact]
    public async Task UnlinkStoreWithoutClearKeepsLocalData()
    {
        var accounts = new SqliteStoreAccountRepository(_database);
        await accounts.SaveAuthenticatedAccountAsync("TEST-01", "Loja teste", "senha-segura", "store-uid");
        await accounts.RemoveAccountAsync(clearLocalData: false);
        Assert.False(await accounts.HasAccountAsync());
        Assert.NotNull(await _products.FindByBarcodeAsync("7891000100103"));
        Assert.Single(await new SqliteAuditLogRepository(_database).SearchAsync("Loja desvinculada"));
    }

    [Fact]
    public async Task UnlinkStoreRollsBackAllChangesIfCleanupFails()
    {
        var accounts = new SqliteStoreAccountRepository(_database);
        await accounts.SaveAuthenticatedAccountAsync("TEST-01", "Loja teste", "senha-segura", "store-uid");
        await using (var connection = _database.CreateConnection())
        {
            await connection.OpenAsync();
            await using var trigger = connection.CreateCommand();
            trigger.CommandText = """
                INSERT INTO Sellers VALUES ('seller', 'Vendedor', 1);
                CREATE TRIGGER PreventProductDelete BEFORE DELETE ON Products
                BEGIN SELECT RAISE(ABORT, 'falha simulada'); END;
                """;
            await trigger.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<SqliteException>(() => accounts.RemoveAccountAsync(clearLocalData: true));
        Assert.True(await accounts.HasAccountAsync());
        Assert.NotNull(await _products.FindByBarcodeAsync("7891000100103"));
        await using var verify = _database.CreateConnection();
        await verify.OpenAsync();
        await using var count = verify.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM Sellers;";
        Assert.Equal(1L, await count.ExecuteScalarAsync());
    }

    [Fact]
    public async Task FirebaseRefreshTokenIsProtectedForTheWindowsUser()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var accounts = new SqliteStoreAccountRepository(_database);
        const string refreshToken = "firebase-refresh-token-test";
        await accounts.SaveAuthenticatedAccountAsync(
            "001-ABC", "Mercado Central", "LojaSegura!2026", "firebase-user-1", "sales", refreshToken);

        Assert.Equal(refreshToken, await accounts.GetRefreshTokenAsync());
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ProtectedRefreshToken FROM StoreAccountCache WHERE Id = 1;";
        var protectedToken = (string?)await command.ExecuteScalarAsync();
        Assert.NotNull(protectedToken);
        Assert.NotEqual(refreshToken, protectedToken);
    }

    [Fact]
    public async Task RestaurantEmployeesCannotShareWaiterNumbers()
    {
        var restaurant = new SqliteRestaurantRepository(_database);
        await restaurant.InitializeAsync();
        var first = new RestaurantEmployeeRecord(Guid.NewGuid(), "Joana", "Garçom", true, "0042");
        var duplicate = new RestaurantEmployeeRecord(Guid.NewGuid(), "Marcos", "Garçom", true, "0042");

        await restaurant.SaveEmployeeAsync(first);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            restaurant.SaveEmployeeAsync(duplicate));

        Assert.Contains("número '0042'", exception.Message);
        Assert.Single(await restaurant.GetEmployeesAsync());
    }

    [Fact]
    public async Task RestaurantAreasCanBeDeactivatedWithoutDeletingStableTableIds()
    {
        var restaurant = new SqliteRestaurantRepository(_database);
        await restaurant.InitializeAsync();
        var mainArea = Assert.Single(await restaurant.GetAreasAsync());
        var areaId = await restaurant.SaveAreaAsync("Varanda");
        await restaurant.SetTableCountAsync(areaId, 2);
        var originalTableIds = (await restaurant.GetTablesAsync(areaId))
            .Select(table => table.Id)
            .ToArray();

        await restaurant.SetAreaActiveAsync(areaId, false);

        var inactiveArea = Assert.Single((await restaurant.GetAreasAsync()).Where(area => area.Id == areaId));
        Assert.False(inactiveArea.IsActive);
        Assert.Empty(await restaurant.GetTablesAsync(areaId));
        var retainedTables = await restaurant.GetTablesAsync(areaId, includeInactive: true);
        Assert.Equal(originalTableIds, retainedTables.Select(table => table.Id));
        Assert.All(retainedTables, table => Assert.True(table.IsActive));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            restaurant.SetTableCountAsync(areaId, 3));

        await restaurant.SetAreaActiveAsync(areaId, true);
        var reactivatedArea = Assert.Single((await restaurant.GetAreasAsync()).Where(area => area.Id == areaId));
        Assert.True(reactivatedArea.IsActive);
        Assert.Equal(originalTableIds, (await restaurant.GetTablesAsync(areaId)).Select(table => table.Id));
        Assert.True(mainArea.IsActive);

        var areaEvents = (await new SqliteSyncOutboxRepository(_database).GetPendingAsync())
            .Where(item => item.Entity == "area")
            .ToArray();
        Assert.Equal(3, areaEvents.Length);
    }

    [Fact]
    public async Task RestaurantItemPaymentsTrackFractionalQuantitiesAndRounding()
    {
        var restaurant = new SqliteRestaurantRepository(_database);
        await restaurant.InitializeAsync();
        var area = Assert.Single(await restaurant.GetAreasAsync());
        var table = (await restaurant.GetTablesAsync(area.Id))[0];
        var menu = new RestaurantMenuItemRecord(Guid.NewGuid(), "Porção", "Comida", 0.05m, true);
        await restaurant.SaveMenuItemAsync(menu);
        await restaurant.AddOrderItemAsync(table.Id, menu.Id, 3m);
        await restaurant.OpenCashSessionAsync(0m);
        var order = (await restaurant.GetOpenOrderAsync(table.Id))! with { WaiterServiceChargeEnabled = true };
        var item = Assert.Single(order.Items);
        var first = new[] { new RestaurantItemPaymentSelection(item.Id, 1m) };
        Assert.Equal(0.06m, order.ItemPaymentAmount(first));
        var payment = await restaurant.PayOrderItemsAsync(table.Id, PaymentMethod.Pix, first, true, 0.06m);
        Assert.Equal(0.11m, payment.RemainingTotal);
        order = (await restaurant.GetOpenOrderAsync(table.Id))!;
        Assert.Equal(1m, Assert.Single(order.Items).PaidQuantity);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            restaurant.PayOrderItemsAsync(table.Id, PaymentMethod.Cash, [new(item.Id, 3m)], true, 0.11m));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            restaurant.PayOrderItemsAsync(table.Id, PaymentMethod.Cash, [new(item.Id, 1m)], true, 0.06m));
        var second = new[] { new RestaurantItemPaymentSelection(item.Id, 0.5m) };
        Assert.Equal(0.03m, order.ItemPaymentAmount(second));
        await restaurant.PayOrderItemsAsync(table.Id, PaymentMethod.Cash, second, true, 0.03m);
        order = (await restaurant.GetOpenOrderAsync(table.Id))!;
        Assert.Equal(1.5m, Assert.Single(order.Items).PaidQuantity);
        var last = new[] { new RestaurantItemPaymentSelection(item.Id, 1.5m) };
        Assert.Equal(0.08m, order.ItemPaymentAmount(last));
        await restaurant.PayOrderItemsAsync(table.Id, PaymentMethod.Cash, last, true, 0.08m);
        Assert.Null(await restaurant.GetOpenOrderAsync(table.Id));
        var cash = (await restaurant.GetOpenCashSessionAsync())!;
        Assert.Equal(0.11m, cash.CashSales);
        Assert.Equal(0.06m, cash.OtherSales);
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync();
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM RestaurantPaymentItems;";
        Assert.Equal(3L, await count.ExecuteScalarAsync());
    }

    [Fact]
    public async Task RestaurantItemPaymentsBlockUnallocatedPaymentsAndRollbackInvalidSelections()
    {
        var restaurant = new SqliteRestaurantRepository(_database);
        await restaurant.InitializeAsync();
        var table = (await restaurant.GetTablesAsync(Assert.Single(await restaurant.GetAreasAsync()).Id))[0];
        var menu = new RestaurantMenuItemRecord(Guid.NewGuid(), "Bebida", "Bebida", 10m, true);
        await restaurant.SaveMenuItemAsync(menu);
        await restaurant.AddOrderItemAsync(table.Id, menu.Id, 3m);
        await restaurant.OpenCashSessionAsync(0m);
        var item = Assert.Single((await restaurant.GetOpenOrderAsync(table.Id))!.Items);
        await restaurant.PayOrderItemsAsync(table.Id, PaymentMethod.Pix, [new(item.Id, 1m)], false, 10m);
        await restaurant.PayOrderAsync(table.Id, PaymentMethod.Cash, 5m, false);
        var order = (await restaurant.GetOpenOrderAsync(table.Id))!;
        Assert.True(order.HasUnallocatedPayments);
        Assert.Equal(1m, Assert.Single(order.Items).PaidQuantity);
        Assert.Equal(15m, order.RemainingTotal);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            restaurant.PayOrderItemsAsync(table.Id, PaymentMethod.Pix, [new(item.Id, 1m)], false, 10m));
        Assert.Equal(15m, (await restaurant.GetOpenOrderAsync(table.Id))!.RemainingTotal);
        await restaurant.PayOrderAsync(table.Id, PaymentMethod.Pix, 15m, false);
        Assert.Null(await restaurant.GetOpenOrderAsync(table.Id));

        await restaurant.AddOrderItemAsync(table.Id, menu.Id, 2m);
        var newItem = Assert.Single((await restaurant.GetOpenOrderAsync(table.Id))!.Items);
        await restaurant.PayOrderAsync(table.Id, PaymentMethod.Pix, 5m, false);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            restaurant.PayOrderItemsAsync(table.Id, PaymentMethod.Cash, [new(newItem.Id, 1m)], false, 10m));
        Assert.Equal(15m, (await restaurant.GetOpenOrderAsync(table.Id))!.RemainingTotal);
    }

    [Fact]
    public async Task RestaurantItemPaymentsRejectDuplicateAndForeignItemsWithoutRecordingPayment()
    {
        var restaurant = new SqliteRestaurantRepository(_database);
        await restaurant.InitializeAsync();
        var tables = await restaurant.GetTablesAsync(Assert.Single(await restaurant.GetAreasAsync()).Id);
        var menu = new RestaurantMenuItemRecord(Guid.NewGuid(), "Item", "Comida", 10m, true);
        await restaurant.SaveMenuItemAsync(menu);
        await restaurant.AddOrderItemAsync(tables[0].Id, menu.Id, 2m);
        await restaurant.AddOrderItemAsync(tables[1].Id, menu.Id, 1m);
        await restaurant.OpenCashSessionAsync(0m);
        var item = Assert.Single((await restaurant.GetOpenOrderAsync(tables[0].Id))!.Items);
        var foreign = Assert.Single((await restaurant.GetOpenOrderAsync(tables[1].Id))!.Items);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            restaurant.PayOrderItemsAsync(tables[0].Id, PaymentMethod.Cash,
                [new(item.Id, 1m), new(item.Id, 1m)], false, 20m));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            restaurant.PayOrderItemsAsync(tables[0].Id, PaymentMethod.Cash, [new(foreign.Id, 1m)], false, 10m));
        Assert.Equal(0m, (await restaurant.GetOpenOrderAsync(tables[0].Id))!.PaidTotal);
        Assert.Equal(0m, (await restaurant.GetOpenCashSessionAsync())!.CashSales);
    }

    [Fact]
    public async Task RestaurantItemPaymentsConcurrentSelectionsCannotPayTheSameQuantityTwice()
    {
        var restaurant = new SqliteRestaurantRepository(_database);
        await restaurant.InitializeAsync();
        var table = (await restaurant.GetTablesAsync(Assert.Single(await restaurant.GetAreasAsync()).Id))[0];
        var menu = new RestaurantMenuItemRecord(Guid.NewGuid(), "Item", "Comida", 10m, true);
        await restaurant.SaveMenuItemAsync(menu);
        await restaurant.AddOrderItemAsync(table.Id, menu.Id, 1m);
        await restaurant.AddOrderItemAsync(table.Id, menu.Id, 1m);
        await restaurant.OpenCashSessionAsync(0m);
        var item = (await restaurant.GetOpenOrderAsync(table.Id))!.Items[0];
        async Task<bool> TryPay()
        {
            try
            {
                await restaurant.PayOrderItemsAsync(table.Id, PaymentMethod.Pix, [new(item.Id, 1m)], false, 10m);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
        var outcomes = await Task.WhenAll(Task.Run(TryPay), Task.Run(TryPay));
        Assert.Single(outcomes.Where(result => result));
        var order = (await restaurant.GetOpenOrderAsync(table.Id))!;
        Assert.Equal(10m, order.PaidTotal);
        Assert.Equal(1m, order.Items[0].PaidQuantity);
        Assert.Equal(10m, order.RemainingTotal);
    }

    [Fact]
    public async Task RestaurantCounterTicketShowsSelectedAmountsWithoutChangingPaymentOrPrintState()
    {
        var restaurant = new SqliteRestaurantRepository(_database);
        await restaurant.InitializeAsync();
        var table = (await restaurant.GetTablesAsync(Assert.Single(await restaurant.GetAreasAsync()).Id))[0];
        var menu = new RestaurantMenuItemRecord(Guid.NewGuid(), "Peixe", "Comida", 10m, true);
        await restaurant.SaveMenuItemAsync(menu);
        await restaurant.AddOrderItemAsync(table.Id, menu.Id, 3m, employeeId: null, observation: "Bem frito");
        await restaurant.OpenCashSessionAsync(0m);
        var item = Assert.Single((await restaurant.GetOpenOrderAsync(table.Id))!.Items);
        await restaurant.PayOrderItemsAsync(table.Id, PaymentMethod.Pix, [new(item.Id, 1m)], true, 11m);
        var order = (await restaurant.GetOpenOrderAsync(table.Id))!;
        var before = Assert.Single(await restaurant.GetKitchenPrintBatchesAsync(false));
        var selected = Assert.Single(order.Items) with { Quantity = 0.5m };
        var bytes = RestaurantCounterTicket.Build(order, [selected], table.Number, "Salão", DateTime.Now);
        var text = Encoding.GetEncoding(850).GetString(bytes);
        Assert.Contains("NAO E COMPROVANTE DE PAGAMENTO", text);
        Assert.Contains("Qtd impressa: 0,5", text);
        Assert.Contains("Paga na linha: 1", text);
        Assert.Contains("Soma dos itens selecionados:", text);
        Assert.Contains("5,00", text);
        Assert.Contains("Total pago da comanda:", text);
        Assert.Contains("11,00", text);
        Assert.Contains("Saldo da comanda:", text);
        Assert.Contains("22,00", text);
        Assert.Contains("Bem frito", text);
        Assert.Throws<ArgumentException>(() =>
            RestaurantCounterTicket.Build(order, [selected with { Quantity = 4m }], table.Number, "Salão", DateTime.Now));
        var after = Assert.Single(await restaurant.GetKitchenPrintBatchesAsync(false));
        Assert.Equal(before.PrintBatchNumber, after.PrintBatchNumber);
        Assert.Equal(before.Items.Select(i => i.Id), after.Items.Select(i => i.Id));
        Assert.Equal(11m, (await restaurant.GetOpenOrderAsync(table.Id))!.PaidTotal);
    }

    [Fact]
    public async Task RestaurantTablesMenuOrdersAndCashWorkOffline()
    {
        var restaurant = new SqliteRestaurantRepository(_database);
        await restaurant.InitializeAsync();
        var area = Assert.Single(await restaurant.GetAreasAsync());
        Assert.Equal("Salão", area.Name);
        Assert.Equal(12, area.TableCount);

        await restaurant.SetTableCountAsync(area.Id, 2);
        var tables = await restaurant.GetTablesAsync(area.Id);
        Assert.Equal(2, tables.Count);
        var firstTable = tables[0];
        var employee = new RestaurantEmployeeRecord(
            Guid.NewGuid(), "Joana", "Atendente", true, "0042", "firebase-waiter-uid", area.Id.ToString());
        var inventoryProduct = await _products.FindByScaleCodeAsync("01234");
        Assert.NotNull(inventoryProduct);
        var menuItem = new RestaurantMenuItemRecord(
            Guid.NewGuid(), "Prato do dia", "Almoço", 24.90m, true, inventoryProduct.Id, 0.25m);
        await restaurant.SaveEmployeeAsync(employee);
        var savedEmployee = Assert.Single(await restaurant.GetEmployeesAsync());
        Assert.Equal(employee.WaiterNumber, savedEmployee.WaiterNumber);
        Assert.Equal(employee.FirebaseUid, savedEmployee.FirebaseUid);
        Assert.Equal(employee.AreaIds, savedEmployee.AreaIds);
        await restaurant.SaveMenuItemAsync(menuItem);
        var menuSyncEvent = Assert.Single((await new SqliteSyncOutboxRepository(_database).GetPendingAsync())
            .Where(item => item.Entity == "menu-item"));
        Assert.Equal("restaurant", menuSyncEvent.Branch);
        Assert.Equal(menuItem.Id.ToString(), menuSyncEvent.EntityId);
        using (var menuPayload = JsonDocument.Parse(menuSyncEvent.Payload))
        {
            var payload = menuPayload.RootElement.GetProperty("payload");
            Assert.Equal(menuItem.Name, payload.GetProperty("name").GetString());
            Assert.Equal(menuItem.Category, payload.GetProperty("category").GetString());
            Assert.Equal(menuItem.Price, payload.GetProperty("price").GetDecimal());
            Assert.True(payload.GetProperty("isActive").GetBoolean());
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            restaurant.AddOrderItemAsync(firstTable.Id, menuItem.Id, 200m));
        Assert.Equal(30m, (await _products.FindByScaleCodeAsync("01234"))!.Stock);
        Assert.Null(await restaurant.GetOpenOrderAsync(firstTable.Id));
        Assert.False(Assert.Single((await restaurant.GetTablesAsync(area.Id)).Where(table => table.Id == firstTable.Id)).IsOccupied);
        await restaurant.AddOrderItemAsync(firstTable.Id, menuItem.Id, 2m, employee.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            restaurant.SetAreaActiveAsync(area.Id, false));
        var order = await restaurant.GetOpenOrderAsync(firstTable.Id);
        Assert.NotNull(order);
        Assert.Equal(49.80m, order.Total);
        Assert.Equal(29.5m, (await _products.FindByScaleCodeAsync("01234"))!.Stock);
        Assert.True(Assert.Single((await restaurant.GetTablesAsync(area.Id)).Where(table => table.Id == firstTable.Id)).IsOccupied);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            restaurant.PayOrderAsync(firstTable.Id, PaymentMethod.Pix));

        await restaurant.OpenCashSessionAsync(50m);
        var partialPayment = await restaurant.PayOrderAsync(firstTable.Id, PaymentMethod.Pix, 20m, true);
        Assert.Equal(20m, partialPayment.Amount);
        Assert.Equal(34.78m, partialPayment.RemainingTotal);
        var partiallyPaidOrder = await restaurant.GetOpenOrderAsync(firstTable.Id);
        Assert.NotNull(partiallyPaidOrder);
        Assert.True(partiallyPaidOrder.WaiterServiceChargeEnabled);
        Assert.Equal(4.98m, partiallyPaidOrder.WaiterServiceCharge);
        Assert.Equal(54.78m, partiallyPaidOrder.Total);
        Assert.True(Assert.Single((await restaurant.GetTablesAsync(area.Id)).Where(table => table.Id == firstTable.Id)).IsOccupied);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            restaurant.PayOrderAsync(firstTable.Id, PaymentMethod.Cash, 1m, false));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            restaurant.PayOrderAsync(firstTable.Id, PaymentMethod.Cash, 35m));
        var payment = await restaurant.PayOrderAsync(firstTable.Id, PaymentMethod.Cash, partialPayment.RemainingTotal, true);
        Assert.Equal(34.78m, payment.Amount);
        Assert.Equal(0m, payment.RemainingTotal);
        Assert.False(Assert.Single((await restaurant.GetTablesAsync(area.Id)).Where(table => table.Id == firstTable.Id)).IsOccupied);

        var cashSession = await restaurant.GetOpenCashSessionAsync();
        Assert.NotNull(cashSession);
        Assert.Equal(34.78m, cashSession.CashSales);
        Assert.Equal(20m, cashSession.OtherSales);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var dashboard = await restaurant.GetDashboardAsync(today.AddDays(-2), today);
        Assert.Equal(54.78m, dashboard.TotalRevenue);
        Assert.Equal(1, dashboard.OrderCount);
        var waiterSales = Assert.Single(await restaurant.GetWaiterSalesAsync(today.AddDays(-2), today));
        Assert.Equal(employee.Name, waiterSales.Name);
        Assert.Equal(1, waiterSales.Orders);
        Assert.Equal(54.78m, waiterSales.Total);
        Assert.Empty(await restaurant.GetWaiterSalesAsync(today.AddDays(-30), today.AddDays(-10)));
        Assert.Equal(54.78m, dashboard.AverageTicket);
        Assert.Equal(3, dashboard.Days.Count);
        Assert.Equal(2, dashboard.Methods.Count);
        var empty = await restaurant.GetDashboardAsync(today.AddDays(-30), today.AddDays(-10));
        Assert.Equal(0m, empty.TotalRevenue);
        await restaurant.AddExpenseAsync("Botijão de gás", "Insumos", 12.50m, true);
        await restaurant.AddExpenseAsync("Fornecedor de bebidas", "Insumos", 100m, false);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            restaurant.AddExpenseAsync("Inválido", "Geral", 0m, false));
        var withExpenses = await restaurant.GetDashboardAsync(today, today);
        Assert.Equal(112.50m, withExpenses.TotalExpenses);
        Assert.Equal(112.50m, Assert.Single(withExpenses.ExpenseCategories!).Total);
        Assert.Equal(12.50m, (await restaurant.GetOpenCashSessionAsync())!.CashExpenses);
        var expenses = await restaurant.GetExpensesAsync(today, today);
        Assert.Equal(2, expenses.Count);
        await restaurant.DeleteExpenseAsync(expenses[0].Id);
        Assert.Single(await restaurant.GetExpensesAsync(today, today));
        var pendingRestaurantSync = await new SqliteSyncOutboxRepository(_database).GetPendingAsync();
        Assert.Equal(9, pendingRestaurantSync.Count(item => item.Branch == "restaurant"));
        await restaurant.CloseCashSessionAsync(cashSession.Id, 50m);
        Assert.Null(await restaurant.GetOpenCashSessionAsync());
    }

    [Fact]
    public async Task WaiterCommandsAreAppliedIdempotentlyToLocalRestaurantOrders()
    {
        var restaurant = new SqliteRestaurantRepository(_database);
        await restaurant.InitializeAsync();
        var area = Assert.Single(await restaurant.GetAreasAsync());
        var table = (await restaurant.GetTablesAsync(area.Id)).First();
        var employee = new RestaurantEmployeeRecord(
            Guid.NewGuid(), "Joana", "Garçom", true, "0042", "firebase-waiter-uid", area.Id.ToString());
        var menuItem = new RestaurantMenuItemRecord(Guid.NewGuid(), "Suco", "Bebidas", 12.50m, true);
        await restaurant.SaveEmployeeAsync(employee);
        await restaurant.SaveMenuItemAsync(menuItem);

        var command = new WaiterCommandRecord(
            Guid.NewGuid().ToString(),
            "addItem",
            area.Id.ToString(),
            table.Id.ToString(),
            employee.FirebaseUid,
            menuItem.Id.ToString(),
            2m,
            "pending",
            DateTimeOffset.UtcNow.ToString("O"),
            "Sem cebola e molho à parte");

        var result = await restaurant.ProcessWaiterCommandAsync(command);
        var duplicateResult = await restaurant.ProcessWaiterCommandAsync(command);

        Assert.Equal("applied", result.Status);
        Assert.Equal(result, duplicateResult);
        Assert.NotNull(result.OrderId);
        var order = await restaurant.GetOpenOrderAsync(table.Id);
        Assert.NotNull(order);
        Assert.Equal(2m, Assert.Single(order.Items).Quantity);
        Assert.Equal(12.50m, order.Items[0].UnitPrice);
        Assert.Equal("Sem cebola e molho à parte", order.Items[0].Observation);
        var pendingEvents = await new SqliteSyncOutboxRepository(_database).GetPendingAsync();
        Assert.Single(pendingEvents.Where(syncEvent => syncEvent.Entity == "order-item"));
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync();
        await using var inspect = connection.CreateCommand();
        inspect.CommandText = "SELECT COUNT(*) FROM RestaurantWaiterCommands WHERE CommandId = $id AND Status = 'applied';";
        inspect.Parameters.AddWithValue("$id", command.Id);
        Assert.Equal(1L, await inspect.ExecuteScalarAsync());
    }

    [Fact]
    public async Task LocalWaiterPinAuthenticatesAndProcessesCommandsOffline()
    {
        var restaurant = new SqliteRestaurantRepository(_database);
        await restaurant.InitializeAsync();
        var area = Assert.Single(await restaurant.GetAreasAsync());
        var table = (await restaurant.GetTablesAsync(area.Id)).First();
        var employee = new RestaurantEmployeeRecord(
            Guid.NewGuid(), "Carlos", "Garçom", true, "0007", null, area.Id.ToString());
        var menuItem = new RestaurantMenuItemRecord(Guid.NewGuid(), "Pastel", "Comidas", 9m, true);
        await restaurant.SaveEmployeeAsync(employee);
        await restaurant.SaveMenuItemAsync(menuItem);

        Assert.Null(await restaurant.VerifyWaiterLocalPinAsync("0007", "1234"));
        await restaurant.SetEmployeeLocalPinAsync(employee.Id, "1234");
        Assert.Null(await restaurant.VerifyWaiterLocalPinAsync("0007", "0000"));
        Assert.Null(await restaurant.VerifyWaiterLocalPinAsync("9999", "1234"));
        var authenticated = await restaurant.VerifyWaiterLocalPinAsync("0007", "1234");
        Assert.NotNull(authenticated);
        Assert.Equal(employee.Id, authenticated!.Id);
        Assert.True(authenticated.HasLocalPin);
        Assert.True((await restaurant.GetEmployeesAsync()).Single(e => e.Id == employee.Id).HasLocalPin);

        var command = new WaiterCommandRecord(
            Guid.NewGuid().ToString(),
            "addItem",
            area.Id.ToString(),
            table.Id.ToString(),
            SqliteRestaurantRepository.CreateLocalWaiterUid(employee.Id),
            menuItem.Id.ToString(),
            3m,
            "pending",
            DateTimeOffset.UtcNow.ToString("O"),
            "Sem pimenta");

        var result = await restaurant.ProcessWaiterCommandAsync(command);
        Assert.Equal("applied", result.Status);
        var order = await restaurant.GetOpenOrderAsync(table.Id);
        Assert.NotNull(order);
        Assert.Equal(3m, Assert.Single(order!.Items).Quantity);
        Assert.Equal("Sem pimenta", order.Items[0].Observation);
        Assert.Equal("Carlos", order.EmployeeName);

        await restaurant.ClearEmployeeLocalPinAsync(employee.Id);
        Assert.Null(await restaurant.VerifyWaiterLocalPinAsync("0007", "1234"));
    }

    [Fact]
    public async Task LocalWaiterCommandIsRejectedOutsideAuthorizedArea()
    {
        var restaurant = new SqliteRestaurantRepository(_database);
        await restaurant.InitializeAsync();
        var area = Assert.Single(await restaurant.GetAreasAsync());
        var otherAreaId = await restaurant.SaveAreaAsync("Varanda");
        await restaurant.SetTableCountAsync(otherAreaId, 3);
        var otherTable = (await restaurant.GetTablesAsync(otherAreaId)).First();
        var employee = new RestaurantEmployeeRecord(
            Guid.NewGuid(), "Carlos", "Garçom", true, "0007", null, area.Id.ToString());
        var menuItem = new RestaurantMenuItemRecord(Guid.NewGuid(), "Pastel", "Comidas", 9m, true);
        await restaurant.SaveEmployeeAsync(employee);
        await restaurant.SaveMenuItemAsync(menuItem);

        var command = new WaiterCommandRecord(
            Guid.NewGuid().ToString(),
            "addItem",
            otherAreaId.ToString(),
            otherTable.Id.ToString(),
            SqliteRestaurantRepository.CreateLocalWaiterUid(employee.Id),
            menuItem.Id.ToString(),
            1m,
            "pending",
            DateTimeOffset.UtcNow.ToString("O"));

        var result = await restaurant.ProcessWaiterCommandAsync(command);
        Assert.Equal("rejected", result.Status);
        Assert.Contains("área", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await restaurant.GetOpenOrderAsync(otherTable.Id));
    }

    [Fact]
    public async Task LocalWaiterServerServesWaiterAppEndToEnd()
    {
        var restaurant = new SqliteRestaurantRepository(_database);
        await restaurant.InitializeAsync();
        var area = Assert.Single(await restaurant.GetAreasAsync());
        var table = (await restaurant.GetTablesAsync(area.Id)).First();
        var employee = new RestaurantEmployeeRecord(
            Guid.NewGuid(), "Carlos", "Garçom", true, "0007", null, area.Id.ToString());
        var menuItem = new RestaurantMenuItemRecord(Guid.NewGuid(), "Pastel", "Comidas", 9m, true);
        await restaurant.SaveEmployeeAsync(employee);
        await restaurant.SaveMenuItemAsync(menuItem);
        await restaurant.SetEmployeeLocalPinAsync(employee.Id, "1234");

        var port = 18080 + Random.Shared.Next(1000);
        await using var server = new LocalWaiterServer(restaurant, "Loja Teste", port);
        await server.StartAsync();
        Assert.True(server.IsRunning);

        using var http = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
        var status = await http.GetFromJsonAsync<JsonElement>("/api/status");
        Assert.Equal("Loja Teste", status.GetProperty("store").GetString());
        Assert.Contains("SYSMR", await http.GetStringAsync("/"));

        var badLogin = await http.PostAsJsonAsync("/api/login", new { waiterNumber = "0007", pin = "0000" });
        Assert.Equal(HttpStatusCode.Unauthorized, badLogin.StatusCode);

        var loginResponse = await http.PostAsJsonAsync("/api/login", new { waiterNumber = "0007", pin = "1234" });
        var login = await loginResponse.Content.ReadFromJsonAsync<JsonElement>();
        http.DefaultRequestHeaders.Add("X-Waiter-Token", login.GetProperty("token").GetString());

        var areas = await http.GetFromJsonAsync<JsonElement>("/api/areas");
        Assert.Equal(table.Number, areas[0].GetProperty("tables")[0].GetProperty("number").GetInt32());

        var menu = await http.GetFromJsonAsync<JsonElement>("/api/menu");
        Assert.Equal("Pastel", menu[0].GetProperty("name").GetString());

        var commandResponse = await http.PostAsJsonAsync("/api/commands", new
        {
            type = "addItem",
            areaId = area.Id,
            tableId = table.Id,
            menuItemId = menuItem.Id,
            quantity = 2,
            observation = "Sem pimenta"
        });
        var commandResult = await commandResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("applied", commandResult.GetProperty("status").GetString());

        var order = await http.GetFromJsonAsync<JsonElement>($"/api/tables/{table.Id}/order");
        Assert.Equal(2m, order.GetProperty("items")[0].GetProperty("quantity").GetDecimal());
        Assert.Equal("Sem pimenta", order.GetProperty("items")[0].GetProperty("observation").GetString());

        using var anonymous = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/menu")).StatusCode);
    }

    [Fact]
    public async Task LocalMenuIncludesProductDescription()
    {
        var restaurant = new SqliteRestaurantRepository(_database);
        await restaurant.InitializeAsync();
        var area = Assert.Single(await restaurant.GetAreasAsync());
        var employee = new RestaurantEmployeeRecord(Guid.NewGuid(), "Carlos", "Garçom", true, "0007", null, area.Id.ToString());
        await restaurant.SaveEmployeeAsync(employee);
        await restaurant.SaveMenuItemAsync(new RestaurantMenuItemRecord(
            Guid.NewGuid(), "Pastel", "Comidas", 9m, true, Description: "Pastel recheado."));
        await restaurant.SetEmployeeLocalPinAsync(employee.Id, "1234");
        var port = 18080 + Random.Shared.Next(1000);
        await using var server = new LocalWaiterServer(restaurant, "Loja Teste", port);
        await server.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
        var loginResponse = await http.PostAsJsonAsync("/api/login", new { waiterNumber = "0007", pin = "1234" });
        loginResponse.EnsureSuccessStatusCode();
        var login = await loginResponse.Content.ReadFromJsonAsync<JsonElement>();
        http.DefaultRequestHeaders.Add("X-Waiter-Token", login.GetProperty("token").GetString());
        var menu = await http.GetFromJsonAsync<JsonElement>("/api/menu");
        Assert.Equal("Pastel recheado.", menu[0].GetProperty("description").GetString());
    }

    [Fact]
    public void EscPosBuilderEmitsNativeCommandsWithPortugueseAccents()
    {
        var bytes = new EscPosBuilder()
            .AlignCenter().Bold(true).Line("PEDIDO DE COZINHA").Bold(false)
            .DoubleSize(true).Line("05").DoubleSize(false)
            .AlignLeft().WrappedLine("2x Pastel de queijo - Obs.: coração, açúcar e limão")
            .Separator()
            .Feed(3).Cut()
            .Build();

        // ESC @ (init) e ESC t 2 (code page PC850 para acentos)
        Assert.Equal([0x1B, 0x40, 0x1B, 0x74, 0x02], bytes.Take(5).ToArray());
        // Texto acentuado codificado em PC850: ç=0x87, ã=0xC6, é=0x82
        var text = Encoding.GetEncoding(850).GetString(bytes);
        Assert.Contains("COZINHA", text);
        Assert.Contains("ç", text);
        Assert.Contains("ã", text);
        // Corte parcial GS V 66 3 no final
        Assert.Equal([0x1D, 0x56, 0x42, 0x03], bytes.TakeLast(4).ToArray());
    }

    [Fact]
    public void EscPosRasterLogoProducesPackedImageCommand()
    {
        var bytes = new EscPosBuilder().AlignLeft().RasterBitmap(8, 2, [0x80, 0x01]).Build();
        Assert.Contains("1D763000010002008001", Convert.ToHexString(bytes));
        Assert.Throws<ArgumentException>(() => new EscPosBuilder().RasterBitmap(8, 2, [0x80]));
    }

    [Fact]
    public void EscPosBuilderWrapsLongItemNamesWithinRollWidth()
    {
        var lines = EscPosBuilder.Wrap(
            "2x Porção de tilápia frita com limão e molho especial da casa",
            EscPosBuilder.LineWidth80Mm);
        Assert.True(lines.Count > 1);
        Assert.All(lines, line => Assert.True(line.Length <= EscPosBuilder.LineWidth80Mm));
        Assert.Equal("2x Porção de tilápia frita com limão e molho", lines[0]);
    }

    [Fact]
    public void EscPosPrinterParsesNetworkTargets()
    {
        Assert.True(EscPosPrinter.TryParseNetworkTarget("192.168.0.50:9100", out var host, out var port));
        Assert.Equal("192.168.0.50", host);
        Assert.Equal(9100, port);
        Assert.False(EscPosPrinter.TryParseNetworkTarget("MP-POS80 2", out _, out _));
        Assert.False(EscPosPrinter.TryParseNetworkTarget("Padrão do Windows", out _, out _));
        Assert.False(EscPosPrinter.TryParseNetworkTarget(null, out _, out _));
    }

    [Fact]
    public void EscPosBuilderEmitsQrCodeCommands()
    {
        var bytes = new EscPosBuilder()
            .AlignCenter()
            .QrCode("http://192.168.0.10:8080")
            .Cut()
            .Build();

        // Sequência GS ( k do QR Code: modelo 2, tamanho, correção M, dados, imprimir
        var text = Convert.ToHexString(bytes);
        Assert.Contains("1D286B040031413200", text);       // função 65: modelo 2
        Assert.Contains("1D286B0300314306", text);         // função 67: módulo tamanho 6
        Assert.Contains("1D286B0300314531", text);         // função 69: correção M
        Assert.Contains("1D286B", text);                   // função 80: dados
        Assert.Contains("1D286B0300315130", text);         // função 81: imprimir
        Assert.Equal([0x1D, 0x56, 0x42, 0x03], bytes.TakeLast(4).ToArray()); // corte
    }

    [Fact]
    public void EscPosBuilderEmitsQrCodeAsBitmap()
    {
        var bytes = new EscPosBuilder()
            .AlignCenter()
            .QrCodeBitmap("http://192.168.0.10:8080")
            .Cut()
            .Build();

        // Cada faixa da imagem usa GS v 0 (1D 76 30 00) — raster bitmap
        var text = Convert.ToHexString(bytes);
        // Várias faixas (uma por linha da imagem)
        var occurrences = text.Split("1D763000").Length - 1;
        Assert.True(occurrences > 100, $"Esperava muitas faixas de bitmap, veio {occurrences}");
        Assert.Equal([0x1D, 0x56, 0x42, 0x03], bytes.TakeLast(4).ToArray()); // corte
    }

    [Fact]
    public async Task PrintBatchNumberIsSequentialAndSharedPerSubmission()
    {
        var restaurant = new SqliteRestaurantRepository(_database);
        await restaurant.InitializeAsync();
        var area = Assert.Single(await restaurant.GetAreasAsync());
        var table = (await restaurant.GetTablesAsync(area.Id)).First();
        var menuItem = new RestaurantMenuItemRecord(Guid.NewGuid(), "Pastel", "Comidas", 9m, true);
        await restaurant.SaveMenuItemAsync(menuItem);

        // Primeiro envio: dois itens de uma vez → mesmo número de lote
        await restaurant.AddOrderItemAsync(table.Id, menuItem.Id, 1m);
        await restaurant.AddOrderItemAsync(table.Id, menuItem.Id, 2m);
        var firstBatch = Assert.Single(await restaurant.GetKitchenPrintBatchesAsync(failedOnly: false));
        var firstNumber = await restaurant.AssignPrintBatchNumberAsync(
            firstBatch.Items.Select(item => item.Id));
        Assert.Equal(1, firstNumber);

        // Itens já numerados não recebem número novo (reimpressão mantém o número)
        var secondAssign = await restaurant.AssignPrintBatchNumberAsync(
            firstBatch.Items.Select(item => item.Id));
        Assert.Equal(1, secondAssign);
        var reread = Assert.Single(await restaurant.GetKitchenPrintBatchesAsync(failedOnly: false));
        Assert.Equal(1, reread.PrintBatchNumber);

        // Segundo envio (outro item depois) → número novo
        await restaurant.AddOrderItemAsync(table.Id, menuItem.Id, 3m);
        var pending = await restaurant.GetKitchenPrintBatchesAsync(failedOnly: false);
        var newItems = pending.SelectMany(batch => batch.Items)
            .Where(item => !firstBatch.Items.Select(i => i.Id).Contains(item.Id))
            .ToArray();
        Assert.Single(newItems);
        var secondNumber = await restaurant.AssignPrintBatchNumberAsync(newItems.Select(item => item.Id));
        Assert.Equal(2, secondNumber);
    }

    [Fact]
    public async Task PrintBatchNumberResetsOnlyAfterClosingCashSession()
    {
        var restaurant = new SqliteRestaurantRepository(_database);
        await restaurant.InitializeAsync();
        var area = Assert.Single(await restaurant.GetAreasAsync());
        var table = (await restaurant.GetTablesAsync(area.Id)).First();
        var item = new RestaurantMenuItemRecord(Guid.NewGuid(), "Pastel", "Comidas", 9m, true);
        await restaurant.SaveMenuItemAsync(item);
        await restaurant.OpenCashSessionAsync(0m);
        await restaurant.AddOrderItemAsync(table.Id, item.Id, 1m);
        var first = Assert.Single(await restaurant.GetKitchenPrintBatchesAsync(false));
        Assert.Equal(1, await restaurant.AssignPrintBatchNumberAsync(first.Items.Select(line => line.Id)));
        await using (var connection = _database.CreateConnection())
        {
            await connection.OpenAsync();
            await using var oldCounter = connection.CreateCommand();
            oldCounter.CommandText = "UPDATE AppSettings SET Value = '2000-01-01:1' WHERE Key = 'printBatch.counter';";
            await oldCounter.ExecuteNonQueryAsync();
        }
        await restaurant.AddOrderItemAsync(table.Id, item.Id, 1m);
        var next = Assert.Single((await restaurant.GetKitchenPrintBatchesAsync(false)).Where(batch => batch.PrintBatchNumber is null));
        Assert.Equal(2, await restaurant.AssignPrintBatchNumberAsync(next.Items.Select(line => line.Id)));
        await restaurant.PayOrderAsync(table.Id, PaymentMethod.Cash);
        var session = (await restaurant.GetOpenCashSessionAsync())!;
        await restaurant.CloseCashSessionAsync(session.Id, 18m);
        await restaurant.OpenCashSessionAsync(0m);
        await restaurant.AddOrderItemAsync(table.Id, item.Id, 1m);
        var after = Assert.Single(await restaurant.GetKitchenPrintBatchesAsync(false));
        Assert.Equal(1, await restaurant.AssignPrintBatchNumberAsync(after.Items.Select(line => line.Id)));
    }

    [Fact]
    public void RestaurantMenuProjectionPublishesExactWebCapabilitiesAndSizes()
    {
        var sized = new RestaurantMenuItemRecord(Guid.NewGuid(), "Tilápia", "Porções", 40m, true, PriceM: 40m, PriceG: 55m, Description: "Filé de tilápia, sem espinho.");
        var single = new RestaurantMenuItemRecord(Guid.NewGuid(), "Água", "Bebidas", 3m, true);
        var json = JsonSerializer.Serialize(FirebaseStoreAccountClient.CreateRestaurantMenuProjection([sized, single]),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.True(root.GetProperty("capabilities").GetProperty("submitOrder").GetBoolean());
        var items = root.GetProperty("items");
        Assert.Equal("Filé de tilápia, sem espinho.", items.GetProperty(sized.Id.ToString()).GetProperty("description").GetString());
        var sizes = items.GetProperty(sized.Id.ToString()).GetProperty("sizes");
        Assert.Equal(40m, sizes.GetProperty("M").GetDecimal());
        Assert.Equal(55m, sizes.GetProperty("G").GetDecimal());
        Assert.False(items.GetProperty(single.Id.ToString()).TryGetProperty("sizes", out _));
    }

    [Fact]
    public async Task SubmitOrderAppliesAtomicallyWithSizesOptionsAndSharedPrintNumber()
    {
        var restaurant = new SqliteRestaurantRepository(_database);
        await restaurant.InitializeAsync();
        var area = Assert.Single(await restaurant.GetAreasAsync());
        var table = (await restaurant.GetTablesAsync(area.Id)).First();
        var employee = new RestaurantEmployeeRecord(
            Guid.NewGuid(), "Joana", "Garçom", true, "0042", "firebase-waiter-uid", area.Id.ToString());
        var option = new RestaurantMenuOptionRecord(Guid.NewGuid(), "Gelo e limão", 1.50m);
        var fish = new RestaurantMenuItemRecord(Guid.NewGuid(), "Tilápia", "Porções", 40m, true, PriceM: 40m, PriceG: 55m);
        var drink = new RestaurantMenuItemRecord(Guid.NewGuid(), "Bebida", "Bebidas", 6m, true, Options: [option], PrintTarget: "bar");
        await restaurant.SaveEmployeeAsync(employee);
        await restaurant.SaveMenuItemAsync(fish);
        await restaurant.SaveMenuItemAsync(drink);
        var commandId = Guid.NewGuid().ToString();
        var json = JsonSerializer.Serialize(new
        {
            id = commandId, type = "submitOrder", employeeUid = employee.FirebaseUid,
            areaId = area.Id, tableId = table.Id, status = "pending", createdAt = "2026-11-12T12:00:00.000Z", customerName = "Ana",
            items = new object[]
            {
                new { menuItemId = fish.Id, size = "M", quantity = 1, observation = "Bem frita" },
                new { menuItemId = drink.Id, quantity = 2, options = new[] { new { optionId = option.Id, quantity = 1, extraPrice = 0m } } }
            }
        });
        var command = JsonSerializer.Deserialize<WaiterCommandRecord>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var result = await restaurant.ProcessWaiterCommandAsync(command);
        Assert.Equal("applied", result.Status);
        Assert.Equal(result, await restaurant.ProcessWaiterCommandAsync(command));
        var order = (await restaurant.GetOpenOrderAsync(table.Id))!;
        Assert.Equal("Ana", order.CustomerName);
        Assert.Equal("Joana", order.EmployeeName);
        var savedTable = Assert.Single((await restaurant.GetTablesAsync(area.Id)).Where(value => value.Id == table.Id));
        Assert.Equal("Ana", savedTable.CustomerName);
        Assert.Equal("Joana", savedTable.EmployeeName);
        Assert.Equal(3, order.Items.Count);
        Assert.Equal(53.50m, order.Subtotal);
        Assert.Contains(order.Items, item => item.Name == "Tilápia (M)" && item.Observation == "Bem frita" && item.UnitPrice == 40m);
        var batch = Assert.Single(await restaurant.GetKitchenPrintBatchesAsync(false));
        Assert.NotNull(batch.PrintBatchNumber);
        Assert.Equal("Ana", batch.CustomerName);
        Assert.Single(batch.Items.Where(item => item.PrintTarget == "kitchen"));
        Assert.Equal(2, batch.Items.Count(item => item.PrintTarget == "bar"));

        var anotherEmployee = new RestaurantEmployeeRecord(
            Guid.NewGuid(), "Pedro", "Garçom", true, "0043", "firebase-waiter-2", area.Id.ToString());
        await restaurant.SaveEmployeeAsync(anotherEmployee);
        var another = command with { Id = Guid.NewGuid().ToString(), EmployeeUid = anotherEmployee.FirebaseUid,
            CustomerName = "Outro", Items = [new(fish.Id.ToString(), 1, "G")] };
        Assert.Equal("applied", (await restaurant.ProcessWaiterCommandAsync(another)).Status);
        var batches = await restaurant.GetKitchenPrintBatchesAsync(false);
        Assert.Equal(2, batches.Count);
        Assert.Contains(batches, value => value.EmployeeName == "Joana");
        Assert.Contains(batches, value => value.EmployeeName == "Pedro");
        Assert.Equal("Ana", (await restaurant.GetOpenOrderAsync(table.Id))!.CustomerName);
        Assert.Equal("Joana", (await restaurant.GetOpenOrderAsync(table.Id))!.EmployeeName);
        Assert.NotEqual(batches[0].PrintBatchNumber, batches[1].PrintBatchNumber);
        Assert.Contains((await restaurant.GetOpenOrderAsync(table.Id))!.Items, item => item.Name == "Tilápia (G)" && item.UnitPrice == 55m);
        await restaurant.MarkKitchenPrintAsync(batch.Items.Where(item => item.PrintTarget == "kitchen").Select(item => item.Id), true);
        await restaurant.MarkKitchenPrintAsync(batch.Items.Where(item => item.PrintTarget == "bar").Select(item => item.Id), false, "Falha de impressão");
        var failed = Assert.Single(await restaurant.GetKitchenPrintBatchesAsync(true));
        Assert.Equal(batch.PrintBatchNumber, failed.PrintBatchNumber);
        Assert.All(failed.Items, item => Assert.Equal("bar", item.PrintTarget));
    }

    [Fact]
    public async Task SubmitOrderRejectsWholeTransactionWhenOneItemIsInvalid()
    {
        var restaurant = new SqliteRestaurantRepository(_database);
        await restaurant.InitializeAsync();
        var area = Assert.Single(await restaurant.GetAreasAsync());
        var table = (await restaurant.GetTablesAsync(area.Id)).First();
        var employee = new RestaurantEmployeeRecord(Guid.NewGuid(), "Joana", "Garçom", true, "0042", "uid", area.Id.ToString());
        var product = await _products.FindByScaleCodeAsync("01234");
        Assert.NotNull(product);
        var item = new RestaurantMenuItemRecord(Guid.NewGuid(), "Tilápia", "Porções", 40m, true, product.Id, 1m, PriceM: 40m, PriceG: 55m);
        await restaurant.SaveEmployeeAsync(employee);
        await restaurant.SaveMenuItemAsync(item);
        var command = new WaiterCommandRecord(Guid.NewGuid().ToString(), "submitOrder", area.Id.ToString(), table.Id.ToString(),
            employee.FirebaseUid, null, 0m, "pending", DateTimeOffset.UtcNow.ToString("O"),
            Items: [new(item.Id.ToString(), 1m, "M"), new(item.Id.ToString(), 1m, "X")]);
        var result = await restaurant.ProcessWaiterCommandAsync(command);
        Assert.Equal("rejected", result.Status);
        Assert.Equal(result, await restaurant.ProcessWaiterCommandAsync(command));
        Assert.Null(await restaurant.GetOpenOrderAsync(table.Id));
        Assert.Equal(30m, (await _products.FindByScaleCodeAsync("01234"))!.Stock);
        Assert.Empty(await restaurant.GetKitchenPrintBatchesAsync(false));
    }

    [Fact]
    public async Task PesqueiroCatalogReplacesActiveMenuAndPreservesHistory()
    {
        var restaurant = new SqliteRestaurantRepository(_database);
        await restaurant.InitializeAsync();
        var area = Assert.Single(await restaurant.GetAreasAsync());
        var table = (await restaurant.GetTablesAsync(area.Id)).First();
        var old = new RestaurantMenuItemRecord(Guid.NewGuid(), "Produto antigo", "Teste", 10m, true);
        await restaurant.SaveMenuItemAsync(old);
        await restaurant.AddOrderItemAsync(table.Id, old.Id, 1m);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restaurant.ReplaceWithPesqueiroMenuAsync());
        Assert.Contains(await restaurant.GetMenuItemsAsync(true), item => item.Id == old.Id);
        await restaurant.RemoveOrderItemAsync(table.Id, Assert.Single((await restaurant.GetOpenOrderAsync(table.Id))!.Items).Id);
        await restaurant.ReplaceWithPesqueiroMenuAsync();
        var items = await restaurant.GetMenuItemsAsync(true);
        Assert.DoesNotContain(items, item => item.Id == old.Id);
        var fish = Assert.Single(items.Where(item => item.Name == "Tilápia"));
        Assert.Equal(40m, fish.PriceM);
        Assert.Equal(55m, fish.PriceG);
        Assert.Equal("kitchen", fish.PrintTarget);
        Assert.Equal("Filé de tilápia, sem espinho.", fish.Description);
        Assert.Equal("Coberta com cheddar e bacon.", Assert.Single(items.Where(item => item.Name == "Batata especial")).Description);
        Assert.Equal("", Assert.Single(items.Where(item => item.Name == "Arroz")).Description);
        Assert.Equal("bar", Assert.Single(items.Where(item => item.Name == "Original lata")).PrintTarget);
        Assert.Equal(7m, Assert.Single(items.Where(item => item.Name == "Brahma Zero lata")).Price);
        Assert.Single(items.Where(item => item.Name == "Pimenta recheada - unidade"));
        Assert.Single(items.Where(item => item.Name == "Pimenta recheada - porção G"));
        Assert.Contains(await new SqliteSyncOutboxRepository(_database).GetPendingAsync(), x => x.Entity == "menu-replaced");
        await restaurant.ReplaceWithPesqueiroMenuAsync();
        Assert.Equal(items.Count, (await restaurant.GetMenuItemsAsync(true)).Count);
    }

    [Fact]
    public async Task PesqueiroDescriptionUpdatePreservesExistingMenuData()
    {
        var restaurant = new SqliteRestaurantRepository(_database);
        await restaurant.InitializeAsync();
        var option = new RestaurantMenuOptionRecord(Guid.NewGuid(), "Extra", 2m);
        var fish = new RestaurantMenuItemRecord(Guid.NewGuid(), "Tilápia", "Porções", 99m, true,
            Options: [option], PrintTarget: "bar", PriceM: 99m, PriceG: 120m);
        var custom = new RestaurantMenuItemRecord(Guid.NewGuid(), "Pacu", "Porções", 42m, true,
            Description: "Descrição personalizada.");
        var other = new RestaurantMenuItemRecord(Guid.NewGuid(), "Misto", "Outra categoria", 50m, true);
        await restaurant.SaveMenuItemAsync(fish);
        await restaurant.SaveMenuItemAsync(custom);
        await restaurant.SaveMenuItemAsync(other);
        Assert.Equal(1, await restaurant.UpdatePesqueiroDescriptionsAsync());
        var saved = await restaurant.GetMenuItemsAsync();
        var updated = Assert.Single(saved.Where(item => item.Id == fish.Id));
        Assert.Equal("Filé de tilápia, sem espinho.", updated.Description);
        Assert.Equal(99m, updated.Price);
        Assert.Equal(120m, updated.PriceG);
        Assert.Equal("bar", updated.PrintTarget);
        Assert.Equal(option, Assert.Single(updated.Options!));
        Assert.Equal(custom.Description, Assert.Single(saved.Where(item => item.Id == custom.Id)).Description);
        Assert.Equal("", Assert.Single(saved.Where(item => item.Id == other.Id)).Description);
        Assert.Equal(0, await restaurant.UpdatePesqueiroDescriptionsAsync());
        Assert.Contains(await new SqliteSyncOutboxRepository(_database).GetPendingAsync(), x => x.Entity == "menu-replaced");
        await restaurant.SaveMenuItemAsync(updated with { Price = 100m, Options = null });
        Assert.Equal(updated.Description, Assert.Single((await restaurant.GetMenuItemsAsync()).Where(item => item.Id == fish.Id)).Description);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            restaurant.SaveMenuItemAsync(updated with { Description = new string('a', 501) }));
    }

    [Fact]
    public async Task MenuDescriptionMigrationPreservesExistingProducts()
    {
        var restaurant = new SqliteRestaurantRepository(_database);
        var item = new RestaurantMenuItemRecord(Guid.NewGuid(), "Tilápia", "Porções", 40m, true, PriceM: 40m, PriceG: 55m);
        await restaurant.SaveMenuItemAsync(item);
        await using (var connection = _database.CreateConnection())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE RestaurantMenuItems DROP COLUMN Description;";
            await command.ExecuteNonQueryAsync();
        }
        await _database.InitializeAsync();
        var saved = Assert.Single((await restaurant.GetMenuItemsAsync()).Where(value => value.Id == item.Id));
        Assert.Equal("", saved.Description);
        Assert.Equal(40m, saved.PriceM);
        Assert.Equal(55m, saved.PriceG);
        Assert.Equal(1, await restaurant.UpdatePesqueiroDescriptionsAsync());
        Assert.Equal("Filé de tilápia, sem espinho.",
            Assert.Single((await restaurant.GetMenuItemsAsync()).Where(value => value.Id == item.Id)).Description);
    }

    [Fact]
    public async Task MenuItemOptionsSplitOrderLinesWithEditableExtraPrice()
    {
        var restaurant = new SqliteRestaurantRepository(_database);
        await restaurant.InitializeAsync();
        var area = Assert.Single(await restaurant.GetAreasAsync());
        var table = (await restaurant.GetTablesAsync(area.Id)).First();
        var employee = new RestaurantEmployeeRecord(
            Guid.NewGuid(), "Joana", "Garçom", true, "0042", "firebase-waiter-uid", area.Id.ToString());
        var lemon = new RestaurantMenuOptionRecord(Guid.NewGuid(), "Gelo e limão", 1.50m);
        var menuItem = new RestaurantMenuItemRecord(
            Guid.NewGuid(), "Refrigerante", "Bebidas", 6m, true,
            Options: [new RestaurantMenuOptionRecord(Guid.NewGuid(), "Gelo", 0m), lemon]);
        await restaurant.SaveEmployeeAsync(employee);
        await restaurant.SaveMenuItemAsync(menuItem);
        var saved = Assert.Single(await restaurant.GetMenuItemsAsync());
        Assert.Equal(2, saved.Options!.Count);

        await restaurant.AddOrderItemAsync(
            table.Id, menuItem.Id, 4m, null, null,
            [new RestaurantOrderSelection(lemon.Id, 2m, 2m)]);
        var order = (await restaurant.GetOpenOrderAsync(table.Id))!;
        Assert.Equal(2, order.Items.Count);
        Assert.Equal(4m * 6m + 2m * 2m, order.Items.Sum(item => item.Quantity * item.UnitPrice));
        Assert.Contains(order.Items, item => item.Name.Contains("Gelo e limão") && item.UnitPrice == 8m);

        await Assert.ThrowsAsync<InvalidOperationException>(() => restaurant.AddOrderItemAsync(
            table.Id, menuItem.Id, 1m, null, null,
            [new RestaurantOrderSelection(lemon.Id, 2m)]));

        var command = new WaiterCommandRecord(
            Guid.NewGuid().ToString(), "addItem", area.Id.ToString(), table.Id.ToString(),
            employee.FirebaseUid, menuItem.Id.ToString(), 2m, "pending",
            DateTimeOffset.UtcNow.ToString("O"),
            Options: [new WaiterCommandOption(lemon.Id.ToString(), 1m)]);
        Assert.Equal("applied", (await restaurant.ProcessWaiterCommandAsync(command)).Status);
        var after = (await restaurant.GetOpenOrderAsync(table.Id))!;
        Assert.Equal(4, after.Items.Count);
    }

    [Fact]
    public async Task KitchenPrintTracksPendingPrintedAndFailedItems()
    {
        var restaurant = new SqliteRestaurantRepository(_database);
        await restaurant.InitializeAsync();
        var area = Assert.Single(await restaurant.GetAreasAsync());
        var table = (await restaurant.GetTablesAsync(area.Id)).First();
        var menuItem = new RestaurantMenuItemRecord(Guid.NewGuid(), "Pastel", "Comidas", 9m, true);
        await restaurant.SaveMenuItemAsync(menuItem);

        await restaurant.AddOrderItemAsync(table.Id, menuItem.Id, 2m);
        var batch = Assert.Single(await restaurant.GetKitchenPrintBatchesAsync(failedOnly: false));
        Assert.Equal(table.Number, batch.TableNumber);
        var ids = batch.Items.Select(item => item.Id).ToArray();

        await restaurant.MarkKitchenPrintAsync(ids, false, "sem papel");
        Assert.Empty(await restaurant.GetKitchenPrintBatchesAsync(failedOnly: false) is { Count: > 0 } all
            ? all.Where(b => b.LastError is null) : []);
        var failed = Assert.Single(await restaurant.GetKitchenPrintBatchesAsync(failedOnly: true));
        Assert.Equal("sem papel", failed.LastError);

        await restaurant.MarkKitchenPrintAsync(ids, true);
        Assert.Empty(await restaurant.GetKitchenPrintBatchesAsync(failedOnly: false));
        Assert.Empty(await restaurant.GetKitchenPrintBatchesAsync(failedOnly: true));

        await restaurant.AddOrderItemAsync(table.Id, menuItem.Id, 1m);
        var next = Assert.Single(await restaurant.GetUnprintedKitchenBatchesForTableAsync(table.Id));
        Assert.Single(next.Items);
        await restaurant.MarkAllKitchenItemsPrintedAsync();
        Assert.Empty(await restaurant.GetKitchenPrintBatchesAsync(failedOnly: false));
    }

    [Fact]
    public async Task DailyOrderNumberIncrementsAndResetsAfterCashClose()
    {
        var restaurant = new SqliteRestaurantRepository(_database);
        await restaurant.InitializeAsync();
        var area = Assert.Single(await restaurant.GetAreasAsync());
        var tables = await restaurant.GetTablesAsync(area.Id);
        var menuItem = new RestaurantMenuItemRecord(Guid.NewGuid(), "Pastel", "Comidas", 9m, true);
        await restaurant.SaveMenuItemAsync(menuItem);

        await restaurant.AddOrderItemAsync(tables[0].Id, menuItem.Id, 1m);
        await restaurant.AddOrderItemAsync(tables[1].Id, menuItem.Id, 1m);
        Assert.Equal(1, (await restaurant.GetOpenOrderAsync(tables[0].Id))!.DailyNumber);
        Assert.Equal(2, (await restaurant.GetOpenOrderAsync(tables[1].Id))!.DailyNumber);

        await restaurant.OpenCashSessionAsync(100m);
        await restaurant.PayOrderAsync(tables[0].Id, PaymentMethod.Cash);
        await restaurant.PayOrderAsync(tables[1].Id, PaymentMethod.Cash);
        var session = (await restaurant.GetOpenCashSessionAsync())!;
        await restaurant.CloseCashSessionAsync(session.Id, 300m);

        await restaurant.AddOrderItemAsync(tables[0].Id, menuItem.Id, 1m);
        Assert.Equal(1, (await restaurant.GetOpenOrderAsync(tables[0].Id))!.DailyNumber);
    }

    [Fact]
    public void DeriveFunctionUrlReplacesLastSegment()
    {
        Assert.Equal(
            "https://resolvestorelogin-wh5xfwpmcq-uc.a.run.app",
            FirebaseStoreAccountClient.DeriveFunctionUrl(
                "https://provisionstore-wh5xfwpmcq-uc.a.run.app",
                "resolveStoreLogin"));
        Assert.Equal(
            "https://updatestorecontactemail-wh5xfwpmcq-uc.a.run.app",
            FirebaseStoreAccountClient.DeriveFunctionUrl(
                "https://provisionstore-wh5xfwpmcq-uc.a.run.app/",
                "updateStoreContactEmail"));
        Assert.Throws<InvalidOperationException>(() =>
            FirebaseStoreAccountClient.DeriveFunctionUrl("http://insecure/provisionStore", "resolveStoreLogin"));
    }

    [Fact]
    public async Task WaiterCommandsCanUpdateObservationAndRemoveOrderItemsIdempotently()
    {
        var restaurant = new SqliteRestaurantRepository(_database);
        await restaurant.InitializeAsync();
        var area = Assert.Single(await restaurant.GetAreasAsync());
        var table = (await restaurant.GetTablesAsync(area.Id)).First();
        var employee = new RestaurantEmployeeRecord(
            Guid.NewGuid(), "Joana", "Garçom", true, "0042", "firebase-waiter-uid", area.Id.ToString());
        var menuItem = new RestaurantMenuItemRecord(Guid.NewGuid(), "Suco", "Bebidas", 12.50m, true);
        await restaurant.SaveEmployeeAsync(employee);
        await restaurant.SaveMenuItemAsync(menuItem);

        var addCommand = new WaiterCommandRecord(
            Guid.NewGuid().ToString(),
            "addItem",
            area.Id.ToString(),
            table.Id.ToString(),
            employee.FirebaseUid,
            menuItem.Id.ToString(),
            1m,
            "pending",
            DateTimeOffset.UtcNow.ToString("O"));
        var addResult = await restaurant.ProcessWaiterCommandAsync(addCommand);
        Assert.Equal("applied", addResult.Status);
        var item = Assert.Single((await restaurant.GetOpenOrderAsync(table.Id))!.Items);

        var updateObservation = new WaiterCommandRecord(
            Guid.NewGuid().ToString(),
            "updateObservation",
            area.Id.ToString(),
            table.Id.ToString(),
            employee.FirebaseUid,
            null,
            0m,
            "pending",
            DateTimeOffset.UtcNow.ToString("O"),
            "Sem gelo",
            item.Id.ToString());
        var updateResult = await restaurant.ProcessWaiterCommandAsync(updateObservation);
        Assert.Equal("applied", updateResult.Status);
        Assert.Equal(updateResult, await restaurant.ProcessWaiterCommandAsync(updateObservation));
        Assert.Equal("Sem gelo", Assert.Single((await restaurant.GetOpenOrderAsync(table.Id))!.Items).Observation);

        var clearObservation = updateObservation with
        {
            Id = Guid.NewGuid().ToString(),
            Observation = string.Empty,
            CreatedAt = DateTimeOffset.UtcNow.ToString("O")
        };
        Assert.Equal("applied", (await restaurant.ProcessWaiterCommandAsync(clearObservation)).Status);
        Assert.Equal(string.Empty, Assert.Single((await restaurant.GetOpenOrderAsync(table.Id))!.Items).Observation);

        var removeItem = new WaiterCommandRecord(
            Guid.NewGuid().ToString(),
            "removeItem",
            area.Id.ToString(),
            table.Id.ToString(),
            employee.FirebaseUid,
            null,
            0m,
            "pending",
            DateTimeOffset.UtcNow.ToString("O"),
            null,
            item.Id.ToString());
        var removeResult = await restaurant.ProcessWaiterCommandAsync(removeItem);
        Assert.Equal("applied", removeResult.Status);
        Assert.Equal(removeResult, await restaurant.ProcessWaiterCommandAsync(removeItem));
        Assert.Null(await restaurant.GetOpenOrderAsync(table.Id));
    }

    [Fact]
    public async Task UnitWaiterCommandsSplitAndRemoveOneAndQueueNoticesIdempotently()
    {
        var restaurant = new SqliteRestaurantRepository(_database);
        await restaurant.InitializeAsync();
        var area = Assert.Single(await restaurant.GetAreasAsync());
        var table = (await restaurant.GetTablesAsync(area.Id)).First();
        var employee = new RestaurantEmployeeRecord(
            Guid.NewGuid(), "Joana", "Garçom", true, "0042", "firebase-waiter-uid", area.Id.ToString());
        var menu = new RestaurantMenuItemRecord(Guid.NewGuid(), "Suco", "Bebidas", 12m, true, PrintTarget: "bar");
        await restaurant.SaveEmployeeAsync(employee);
        await restaurant.SaveMenuItemAsync(menu);
        var add = new WaiterCommandRecord(Guid.NewGuid().ToString(), "addItem", area.Id.ToString(), table.Id.ToString(),
            employee.FirebaseUid, menu.Id.ToString(), 3m, "pending", DateTimeOffset.UtcNow.ToString("O"));
        Assert.Equal("applied", (await restaurant.ProcessWaiterCommandAsync(add)).Status);
        var original = Assert.Single((await restaurant.GetOpenOrderAsync(table.Id))!.Items);
        var edit = new WaiterCommandRecord(Guid.NewGuid().ToString(), "updateOneObservation", area.Id.ToString(),
            table.Id.ToString(), employee.FirebaseUid, null, 0m, "pending", DateTimeOffset.UtcNow.ToString("O"),
            "Sem gelo", original.Id.ToString());
        Assert.Equal("rejected", (await restaurant.ProcessWaiterCommandAsync(edit)).Status);
        Assert.Empty(await restaurant.GetItemChangeNoticesAsync(false));
        await restaurant.MarkKitchenPrintAsync([original.Id], true);
        edit = edit with { Id = Guid.NewGuid().ToString() };
        Assert.Equal("rejected", (await restaurant.ProcessWaiterCommandAsync(edit)).Status);
        await restaurant.MarkKitchenPrintAsync([original.Id], true, printTarget: "bar");
        await restaurant.SaveMenuItemAsync(menu with { PrintTarget = "kitchen" });
        edit = edit with { Id = Guid.NewGuid().ToString() };
        var applied = await restaurant.ProcessWaiterCommandAsync(edit);
        Assert.Equal("applied", applied.Status);
        Assert.Equal(applied, await restaurant.ProcessWaiterCommandAsync(edit));
        var order = (await restaurant.GetOpenOrderAsync(table.Id))!;
        Assert.Equal(2, order.Items.Count);
        Assert.Contains(order.Items, item => item.Id == original.Id && item.Quantity == 2m);
        Assert.Contains(order.Items, item => item.Quantity == 1m && item.Observation == "Sem gelo");
        Assert.Equal(36m, order.Subtotal);
        var notice = Assert.Single(await restaurant.GetItemChangeNoticesAsync(false));
        Assert.Equal("bar", notice.PrintTarget);
        Assert.Equal("observation", notice.Action);
        Assert.Equal("Sem gelo", notice.NewObservation);
        await restaurant.MarkItemChangeNoticeAsync(notice.CommandId, false, "sem papel");
        Assert.Empty(await restaurant.GetItemChangeNoticesAsync(false));
        Assert.Equal("sem papel", Assert.Single(await restaurant.GetItemChangeNoticesAsync(true)).Error);
        await restaurant.MarkItemChangeNoticeAsync(notice.CommandId, true);
        Assert.Empty(await restaurant.GetItemChangeNoticesAsync(true));
        var remove = edit with { Id = Guid.NewGuid().ToString(), Type = "removeOne", Observation = null };
        var removed = await restaurant.ProcessWaiterCommandAsync(remove);
        Assert.Equal("applied", removed.Status);
        Assert.Equal(removed, await restaurant.ProcessWaiterCommandAsync(remove));
        order = (await restaurant.GetOpenOrderAsync(table.Id))!;
        Assert.Equal(2m, order.Items.Sum(item => item.Quantity));
        Assert.Equal(24m, order.Subtotal);
        Assert.Equal("remove", Assert.Single(await restaurant.GetItemChangeNoticesAsync(false)).Action);
    }

    [Fact]
    public async Task UnitWaiterCommandsProtectStockFractionalItemsAndPartialPayments()
    {
        var restaurant = new SqliteRestaurantRepository(_database);
        await restaurant.InitializeAsync();
        var area = Assert.Single(await restaurant.GetAreasAsync());
        var table = (await restaurant.GetTablesAsync(area.Id)).First();
        var employee = new RestaurantEmployeeRecord(
            Guid.NewGuid(), "Joana", "Garçom", true, "0042", "firebase-waiter-uid", area.Id.ToString());
        var stock = (await _products.FindByScaleCodeAsync("01234"))!;
        var menu = new RestaurantMenuItemRecord(Guid.NewGuid(), "Porção", "Porções", 20m, true, stock.Id, 0.5m);
        await restaurant.SaveEmployeeAsync(employee);
        await restaurant.SaveMenuItemAsync(menu);
        await restaurant.AddOrderItemAsync(table.Id, menu.Id, 3m, employee.Id);
        var item = Assert.Single((await restaurant.GetOpenOrderAsync(table.Id))!.Items);
        Assert.Equal(28.5m, (await _products.FindByScaleCodeAsync("01234"))!.Stock);
        await restaurant.MarkKitchenPrintAsync([item.Id], true, printTarget: "kitchen");
        var remove = new WaiterCommandRecord(Guid.NewGuid().ToString(), "removeOne", area.Id.ToString(), table.Id.ToString(),
            employee.FirebaseUid, null, 0m, "pending", DateTimeOffset.UtcNow.ToString("O"), OrderItemId: item.Id.ToString());
        Assert.Equal("applied", (await restaurant.ProcessWaiterCommandAsync(remove)).Status);
        Assert.Equal(29m, (await _products.FindByScaleCodeAsync("01234"))!.Stock);
        Assert.Equal(2m, Assert.Single((await restaurant.GetOpenOrderAsync(table.Id))!.Items).Quantity);
        await restaurant.OpenCashSessionAsync(0m);
        await restaurant.PayOrderAsync(table.Id, PaymentMethod.Pix, 5m, false);
        remove = remove with { Id = Guid.NewGuid().ToString() };
        Assert.Equal("rejected", (await restaurant.ProcessWaiterCommandAsync(remove)).Status);
        var edit = remove with { Id = Guid.NewGuid().ToString(), Type = "updateOneObservation", Observation = "Sem sal" };
        Assert.Equal("rejected", (await restaurant.ProcessWaiterCommandAsync(edit)).Status);
        Assert.Equal(29m, (await _products.FindByScaleCodeAsync("01234"))!.Stock);
        Assert.Single(await restaurant.GetItemChangeNoticesAsync(false));

        var other = (await restaurant.GetTablesAsync(area.Id)).Skip(1).First();
        await restaurant.AddOrderItemAsync(other.Id, menu.Id, 0.5m, employee.Id);
        var fractional = Assert.Single((await restaurant.GetOpenOrderAsync(other.Id))!.Items);
        await restaurant.MarkKitchenPrintAsync([fractional.Id], true, printTarget: "kitchen");
        remove = remove with { Id = Guid.NewGuid().ToString(), TableId = other.Id.ToString(), OrderItemId = fractional.Id.ToString() };
        Assert.Equal("rejected", (await restaurant.ProcessWaiterCommandAsync(remove)).Status);
        edit = remove with { Id = Guid.NewGuid().ToString(), Type = "updateOneObservation", Observation = "Sem sal" };
        Assert.Equal("rejected", (await restaurant.ProcessWaiterCommandAsync(edit)).Status);
        Assert.Equal(0.5m, Assert.Single((await restaurant.GetOpenOrderAsync(other.Id))!.Items).Quantity);
        Assert.Single(await restaurant.GetItemChangeNoticesAsync(false));
    }

    [Fact]
    public async Task DesktopDisplayGroupsOnlyIdenticalItemsWithoutObservationsAndPreservesBatches()
    {
        var restaurant = new SqliteRestaurantRepository(_database);
        await restaurant.InitializeAsync();
        var area = Assert.Single(await restaurant.GetAreasAsync());
        var table = (await restaurant.GetTablesAsync(area.Id)).First();
        var employee = new RestaurantEmployeeRecord(
            Guid.NewGuid(), "Joana", "Garçom", true, "0042", "firebase-waiter-uid", area.Id.ToString());
        var menu = new RestaurantMenuItemRecord(Guid.NewGuid(), "Peixe", "Porções", 20m, true, PriceM: 20m, PriceG: 30m);
        await restaurant.SaveEmployeeAsync(employee);
        await restaurant.SaveMenuItemAsync(menu);
        var command = new WaiterCommandRecord(Guid.NewGuid().ToString(), "submitOrder", area.Id.ToString(), table.Id.ToString(),
            employee.FirebaseUid, null, 0m, "pending", DateTimeOffset.UtcNow.ToString("O"),
            Items: [new WaiterCommandItem(menu.Id.ToString(), 1, "M")]);
        Assert.Equal("applied", (await restaurant.ProcessWaiterCommandAsync(command)).Status);
        Assert.Equal("applied", (await restaurant.ProcessWaiterCommandAsync(command with { Id = Guid.NewGuid().ToString() })).Status);
        Assert.Equal("applied", (await restaurant.ProcessWaiterCommandAsync(command with
        {
            Id = Guid.NewGuid().ToString(),
            Items = [new WaiterCommandItem(menu.Id.ToString(), 1, "G"), new WaiterCommandItem(menu.Id.ToString(), 1, "M", "Sem sal")]
        })).Status);
        var order = (await restaurant.GetOpenOrderAsync(table.Id))!;
        var groups = RestaurantOrderDisplayGroup.Create(order.Items);
        Assert.Equal(3, groups.Count);
        Assert.Equal(2m, groups[0].Quantity);
        Assert.Equal(40m, groups[0].Total);
        Assert.Equal(2, groups[0].Items.Select(item => item.Id).Distinct().Count());
        Assert.Equal("Peixe (G)", groups[1].Name);
        Assert.Equal("Sem sal", groups[2].Observation);
        Assert.Equal(3, (await restaurant.GetKitchenPrintBatchesAsync(false)).Count);
        var legacy = order.Items[0] with { Id = Guid.NewGuid(), VariantKey = null };
        Assert.Equal(2, RestaurantOrderDisplayGroup.Create([legacy, legacy with { Id = Guid.NewGuid() }]).Count);
    }

    [Fact]
    public async Task WaiterCommandsFromUnassignedAreasAreRejectedWithoutOpeningOrders()
    {
        var restaurant = new SqliteRestaurantRepository(_database);
        await restaurant.InitializeAsync();
        var assignedArea = Assert.Single(await restaurant.GetAreasAsync());
        var otherAreaId = await restaurant.SaveAreaAsync("Varanda");
        await restaurant.SetTableCountAsync(otherAreaId, 2);
        var table = (await restaurant.GetTablesAsync(otherAreaId)).First();
        var employee = new RestaurantEmployeeRecord(
            Guid.NewGuid(), "Joana", "Garçom", true, "0042", "firebase-waiter-uid", assignedArea.Id.ToString());
        var menuItem = new RestaurantMenuItemRecord(Guid.NewGuid(), "Suco", "Bebidas", 12.50m, true);
        await restaurant.SaveEmployeeAsync(employee);
        await restaurant.SaveMenuItemAsync(menuItem);

        var command = new WaiterCommandRecord(
            Guid.NewGuid().ToString(),
            "addItem",
            otherAreaId.ToString(),
            table.Id.ToString(),
            employee.FirebaseUid,
            menuItem.Id.ToString(),
            1m,
            "pending",
            DateTimeOffset.UtcNow.ToString("O"));
        var result = await restaurant.ProcessWaiterCommandAsync(command);
        var repeatedResult = await restaurant.ProcessWaiterCommandAsync(command);

        Assert.Equal("rejected", result.Status);
        Assert.Equal(result, repeatedResult);
        Assert.Contains("não está autorizado", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await restaurant.GetOpenOrderAsync(table.Id));
    }

    [Fact]
    public async Task RemovingOrderItemRestoresLinkedStockAndFreesEmptyTable()
    {
        var restaurant = new SqliteRestaurantRepository(_database);
        await restaurant.InitializeAsync();
        var area = Assert.Single(await restaurant.GetAreasAsync());
        var table = (await restaurant.GetTablesAsync(area.Id)).First();
        var inventoryProduct = await _products.FindByScaleCodeAsync("01234");
        Assert.NotNull(inventoryProduct);
        var menuItem = new RestaurantMenuItemRecord(
            Guid.NewGuid(),
            "Porção teste",
            "Porções",
            25m,
            true,
            inventoryProduct.Id,
            0.5m);
        await restaurant.SaveMenuItemAsync(menuItem);

        await restaurant.AddOrderItemAsync(table.Id, menuItem.Id, 2m);
        Assert.Equal(29m, (await _products.FindByScaleCodeAsync("01234"))!.Stock);
        var order = await restaurant.GetOpenOrderAsync(table.Id);
        Assert.NotNull(order);
        var orderItem = Assert.Single(order.Items);
        Assert.True(Assert.Single((await restaurant.GetTablesAsync(area.Id))
            .Where(currentTable => currentTable.Id == table.Id)).IsOccupied);

        await restaurant.RemoveOrderItemAsync(table.Id, orderItem.Id);

        Assert.Equal(30m, (await _products.FindByScaleCodeAsync("01234"))!.Stock);
        Assert.Null(await restaurant.GetOpenOrderAsync(table.Id));
        Assert.False(Assert.Single((await restaurant.GetTablesAsync(area.Id))
            .Where(currentTable => currentTable.Id == table.Id)).IsOccupied);
        var removalEvent = Assert.Single((await new SqliteSyncOutboxRepository(_database).GetPendingAsync())
            .Where(syncEvent => syncEvent.Entity == "order-item-removed"));
        Assert.Contains(orderItem.Id.ToString(), removalEvent.Payload);
    }

    [Fact]
    public async Task ProductUnitIsPersistedAndLowStockFilterUsesTwoOrLess()
    {
        var lowStockProduct = new Product(
            Guid.NewGuid(),
            "Produto com duas peças",
            "TEST-LOW-2",
            string.Empty,
            1m,
            2m,
            2m,
            1m,
            "Teste",
            string.Empty,
            string.Empty,
            soldByWeight: false,
            unitOfMeasure: "CX");
        await _products.SaveAsync(lowStockProduct);

        var lowStock = await _products.SearchLowStockAsync(2m);
        var storedProduct = Assert.Single(lowStock.Where(product => product.Id == lowStockProduct.Id));
        Assert.Equal("CX", storedProduct.UnitOfMeasure);
        Assert.Equal(2m, storedProduct.Stock);

        var inStock = await _products.SearchLowStockAsync(1m);
        Assert.DoesNotContain(inStock, product => product.Id == lowStockProduct.Id);
    }

    [Fact]
    public async Task ExistingProductAndSaleTablesAreMigratedInPlace()
    {
        var legacyDirectory = Path.Combine(_testDirectory, "legacy");
        Directory.CreateDirectory(legacyDirectory);
        var legacyDatabase = new SqliteDatabase(Path.Combine(legacyDirectory, "legacy.db"));
        await using (var connection = legacyDatabase.CreateConnection())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE Products (
                    Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Barcode TEXT NOT NULL DEFAULT '',
                    ScaleCode TEXT NOT NULL DEFAULT '', CostPrice TEXT NOT NULL, SalePrice TEXT NOT NULL,
                    Stock TEXT NOT NULL, MinimumStock TEXT NOT NULL, Category TEXT NOT NULL DEFAULT '',
                    Ncm TEXT NOT NULL DEFAULT '', Cest TEXT NOT NULL DEFAULT '', SoldByWeight INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE Sales (
                    Id TEXT PRIMARY KEY, CreatedAt TEXT NOT NULL, PaymentMethod TEXT NOT NULL,
                    Total TEXT NOT NULL, CustomerDocument TEXT, SyncStatus INTEGER NOT NULL DEFAULT 0
                );
                """;
            await command.ExecuteNonQueryAsync();
        }

        await legacyDatabase.InitializeAsync();
        var legacyProducts = new SqliteProductRepository(legacyDatabase);
        var apple = await legacyProducts.FindByScaleCodeAsync("01234");
        Assert.NotNull(apple);
        Assert.Equal("KG", apple.UnitOfMeasure);
        await using var verify = legacyDatabase.CreateConnection();
        await verify.OpenAsync();
        await using var inspect = verify.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(Sales);";
        await using var reader = await inspect.ExecuteReaderAsync();
        var hasSellerName = false;
        while (await reader.ReadAsync())
            hasSellerName |= reader.GetString(1) == "SellerName";
        Assert.True(hasSellerName);
    }

    [Fact]
    public async Task ExistingRestaurantPaymentSchemaIsMigratedForMultiplePaymentsPerOrder()
    {
        var legacyPath = Path.Combine(_testDirectory, "restaurant-legacy.db");
        var legacyDatabase = new SqliteDatabase(legacyPath);
        await using (var connection = legacyDatabase.CreateConnection())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE RestaurantPayments (
                    Id TEXT PRIMARY KEY,
                    OrderId TEXT NOT NULL UNIQUE REFERENCES RestaurantOrders(Id),
                    SessionId TEXT NOT NULL REFERENCES RestaurantCashSessions(Id),
                    PaidAt TEXT NOT NULL,
                    PaymentMethod TEXT NOT NULL,
                    Amount TEXT NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync();
        }

        await legacyDatabase.InitializeAsync();
        await using var verify = legacyDatabase.CreateConnection();
        await verify.OpenAsync();
        await using var inspect = verify.CreateCommand();
        inspect.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'RestaurantPayments';";
        var schema = (string?)await inspect.ExecuteScalarAsync();
        Assert.NotNull(schema);
        Assert.DoesNotContain("OrderId TEXT NOT NULL UNIQUE", schema, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("vinte e dois", 22)]
    [InlineData("cinco vírgula cinquenta", 5.5)]
    [InlineData("cinco reais e cinquenta centavos", 5.5)]
    [InlineData("mil e duzentos", 1200)]
    public void PortugueseVoiceNumbersConvertToNumericValues(string transcript, decimal expected)
    {
        Assert.True(PortugueseSpeechParser.TryParseNumber(transcript, out var result));
        Assert.Equal(expected, result);
    }

    [Fact]
    public void PortugueseVoiceDigitNamesConvertToBarcodeDigits()
    {
        Assert.True(PortugueseSpeechParser.TryParseDigits("sete oito nove um zero", out var result));
        Assert.Equal("78910", result);
    }

    private static string CreateScaleLabel(string scaleCode, int grams)
    {
        var body = $"20{scaleCode}{grams:D5}";
        var weightedSum = body.Select((digit, index) => (digit - '0') * (index % 2 == 0 ? 1 : 3)).Sum();
        var checkDigit = (10 - weightedSum % 10) % 10;
        return body + checkDigit;
    }
}
