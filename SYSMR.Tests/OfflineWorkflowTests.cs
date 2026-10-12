using SYSMR.Application.Services;
using SYSMR.Application.Models;
using SYSMR.Domain.Entities;
using SYSMR.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

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
        var employee = new RestaurantEmployeeRecord(Guid.NewGuid(), "Joana", "Atendente", true);
        var inventoryProduct = await _products.FindByScaleCodeAsync("01234");
        Assert.NotNull(inventoryProduct);
        var menuItem = new RestaurantMenuItemRecord(
            Guid.NewGuid(), "Prato do dia", "Almoço", 24.90m, true, inventoryProduct.Id, 0.25m);
        await restaurant.SaveEmployeeAsync(employee);
        await restaurant.SaveMenuItemAsync(menuItem);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            restaurant.AddOrderItemAsync(firstTable.Id, menuItem.Id, 200m));
        Assert.Equal(30m, (await _products.FindByScaleCodeAsync("01234"))!.Stock);
        Assert.Null(await restaurant.GetOpenOrderAsync(firstTable.Id));
        Assert.False(Assert.Single((await restaurant.GetTablesAsync(area.Id)).Where(table => table.Id == firstTable.Id)).IsOccupied);
        await restaurant.AddOrderItemAsync(firstTable.Id, menuItem.Id, 2m, employee.Id);
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
        var pendingRestaurantSync = await new SqliteSyncOutboxRepository(_database).GetPendingAsync();
        Assert.Equal(9, pendingRestaurantSync.Count(item => item.Branch == "restaurant"));
        await restaurant.CloseCashSessionAsync(cashSession.Id, 50m);
        Assert.Null(await restaurant.GetOpenCashSessionAsync());
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
