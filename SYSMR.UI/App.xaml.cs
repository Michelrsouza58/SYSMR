using System.IO;
using System.Windows;
using SYSMR.Application.Services;
using SYSMR.Infrastructure.Persistence;

namespace SYSMR.UI;

public partial class App : System.Windows.Application
{
    private CancellationTokenSource? _syncCancellation;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var loadingWindow = new LoadingWindow();
        loadingWindow.Show();
        try
        {
            loadingWindow.SetStatus("Preparando o banco de dados local...");
            var dataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SYSMR");
            var database = new SqliteDatabase(Path.Combine(dataDirectory, "sysmr.db"));
            await database.InitializeAsync();
            var accessControl = new SqliteAccessControl(database);
            var localAccounts = new SqliteStoreAccountRepository(database);
            if (!await accessControl.IsAdministratorConfiguredAsync())
            {
                loadingWindow.SetStatus("Preparando a segurança inicial...");
                var setupWindow = new AdminPinWindow(accessControl, initialSetup: true);
                if (setupWindow.ShowDialog() != true)
                {
                    loadingWindow.Close();
                    Shutdown();
                    return;
                }
            }

            var saleRepository = new SqliteSaleRepository(database);
            var customerRepository = new SqliteCustomerRepository(database);
            var sellerRepository = new SqliteSellerRepository(database);
            var auditRepository = new SqliteAuditLogRepository(database);
            var syncOutbox = new SqliteSyncOutboxRepository(database);
            StoreAccountIdentity? storeAccount;
            var isSuperAdminLogin = false;
            loadingWindow.SetStatus("Verificando a sessão da loja...");
            storeAccount = await localAccounts.GetRememberedAccountAsync();
            var sessionRestored = storeAccount is not null;
            if (storeAccount is null)
            {
                loadingWindow.SetStatus("Aguardando o login...");
                var storeLoginWindow = new StoreAccountLoginWindow(
                    accessControl,
                    localAccounts,
                    new FirebaseStoreAccountClient(accessControl),
                    auditRepository);
                if (storeLoginWindow.ShowDialog() != true ||
                    (!storeLoginWindow.IsSuperAdminLogin && storeLoginWindow.Account is null))
                {
                    loadingWindow.Close();
                    Shutdown();
                    return;
                }
                storeAccount = storeLoginWindow.Account;
                isSuperAdminLogin = storeLoginWindow.IsSuperAdminLogin;
            }

            loadingWindow.SetStatus("Carregando os módulos...");
            var productRepository = new SqliteProductRepository(database, storeAccount?.StoreType ?? "sales");
            var storeName = storeAccount?.CustomerName ?? "SYSMR";
            var actor = isSuperAdminLogin ? "Super Admin" : storeName;
            var firebase = new FirebaseStoreAccountClient(accessControl);
            var storeTypeSwitcher = new StoreTypeSwitchService(localAccounts, syncOutbox, firebase);
            if (storeAccount is not null)
            {
                _syncCancellation = new CancellationTokenSource();
                _ = new FirebaseSyncWorker(
                    syncOutbox,
                    localAccounts,
                    firebase,
                    auditRepository).RunAsync(storeAccount, _syncCancellation.Token);
            }
            await auditRepository.RecordAsync(
                actor,
                isSuperAdminLogin ? "Acesso Super Admin iniciado" : "Acesso da loja iniciado",
                isSuperAdminLogin
                    ? "Acesso administrativo pelo login SYSMR."
                    : $"Conta {storeAccount!.CustomerNumber}; " +
                      (sessionRestored ? "sessão restaurada automaticamente." : "login realizado."));
            if (storeAccount?.StoreType == "restaurant")
            {
                loadingWindow.SetStatus("Preparando salão, cardápio e caixa...");
                var restaurant = new SqliteRestaurantRepository(database);
                await restaurant.InitializeAsync();
                var restaurantWindow = new RestaurantWindow(
                    restaurant,
                    localAccounts,
                    accessControl,
                    productRepository,
                    syncOutbox,
                    storeTypeSwitcher,
                    storeAccount!,
                    storeName);
                MainWindow = restaurantWindow;
                ShutdownMode = ShutdownMode.OnMainWindowClose;
                restaurantWindow.Show();
                loadingWindow.Close();
                return;
            }

            var window = new MainWindow(
                new ProductCatalogService(productRepository),
                new SalesService(saleRepository),
                productRepository,
                customerRepository,
                sellerRepository,
                accessControl,
                auditRepository,
                localAccounts,
                syncOutbox,
                storeTypeSwitcher,
                storeAccount,
                storeName,
                isSuperAdminLogin);
            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();
            loadingWindow.Close();
        }
        catch (Exception exception)
        {
            loadingWindow.Close();
            MessageBox.Show(
                $"Não foi possível iniciar o SYSMR.\n\n{exception.Message}",
                "Erro de inicialização",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _syncCancellation?.Cancel();
        _syncCancellation?.Dispose();
        base.OnExit(e);
    }
}
