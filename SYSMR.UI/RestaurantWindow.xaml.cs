using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SYSMR.Application.Abstractions;
using SYSMR.Application.Models;
using SYSMR.Domain.Entities;
using SYSMR.Infrastructure;
using SYSMR.Infrastructure.LocalServer;
using SYSMR.Infrastructure.Persistence;

namespace SYSMR.UI;

public partial class RestaurantWindow : Window
{
    private readonly SqliteRestaurantRepository _restaurant;
    private readonly SqliteStoreAccountRepository _localAccounts;
    private readonly SqliteAccessControl _accessControl;
    private readonly IProductRepository _products;
    private readonly SqliteSyncOutboxRepository _syncOutbox;
    private readonly StoreTypeSwitchService _storeTypeSwitcher;
    private readonly StoreAccountIdentity _storeAccount;
    private readonly string _storeName;
    private bool _loadingAreas;
    private bool _isSuperAdminAuthenticated;
    private bool _dashboardEnabled;
    private bool _expensesEnabled;
    private bool _waiterReportEnabled;
    private bool _csvEnabled;
    private bool _lowStockEnabled;
    private bool _blindCloseEnabled;
    private bool _kitchenPrintEnabled;
    private readonly KitchenPrintService _kitchen;
    private LocalWaiterServer? _localServer;
    private bool _serviceChargeEnabled = true;
    private decimal _commissionPercent = 10m;
    private (DateOnly From, DateOnly To)? _lastDashboardRange;

    private static readonly IReadOnlyList<MenuTemplateOption> MenuTemplates =
    [
        new("Pesqueiro", [
            new("Tilápia frita inteira", "Peixes", 89.90m),
            new("Filé de tilápia grelhado", "Peixes", 49.90m),
            new("Isca de peixe", "Porções", 39.90m),
            new("Porção de camarão", "Porções", 59.90m),
            new("Batata frita", "Porções", 29.90m),
            new("Arroz", "Acompanhamentos", 12.90m),
            new("Pirão", "Acompanhamentos", 14.90m),
            new("Vinagrete", "Acompanhamentos", 8.90m),
            new("Água mineral", "Bebidas", 4.00m),
            new("Refrigerante lata", "Bebidas", 7.00m),
            new("Suco natural", "Bebidas", 10.00m),
            new("Café", "Bebidas", 5.00m)
        ]),
        new("Churrascaria", [
            new("Picanha na brasa", "Carnes", 89.90m),
            new("Costela bovina", "Carnes", 74.90m),
            new("Frango assado", "Carnes", 42.90m),
            new("Linguiça artesanal", "Carnes", 34.90m),
            new("Rodízio de carnes", "Rodízio", 79.90m),
            new("Buffet de saladas", "Acompanhamentos", 29.90m),
            new("Farofa da casa", "Acompanhamentos", 9.90m),
            new("Pão de alho", "Entradas", 14.90m),
            new("Água mineral", "Bebidas", 4.00m),
            new("Refrigerante lata", "Bebidas", 7.00m),
            new("Suco natural", "Bebidas", 10.00m),
            new("Cerveja long neck", "Bebidas", 12.00m)
        ]),
        new("Pizzaria", [
            new("Pizza Margherita", "Pizzas", 49.90m),
            new("Pizza Calabresa", "Pizzas", 54.90m),
            new("Pizza Frango com catupiry", "Pizzas", 59.90m),
            new("Pizza Quatro queijos", "Pizzas", 59.90m),
            new("Pizza Portuguesa", "Pizzas", 57.90m),
            new("Borda recheada", "Adicionais", 12.00m),
            new("Calzone", "Massas", 39.90m),
            new("Refrigerante 1 litro", "Bebidas", 12.00m),
            new("Refrigerante lata", "Bebidas", 7.00m),
            new("Suco natural", "Bebidas", 10.00m),
            new("Água mineral", "Bebidas", 4.00m)
        ]),
        new("Lanchonete / Hamburgueria", [
            new("Hambúrguer clássico", "Hambúrgueres", 29.90m),
            new("Cheeseburger", "Hambúrgueres", 32.90m),
            new("X-salada", "Lanches", 27.90m),
            new("Sanduíche de frango", "Lanches", 26.90m),
            new("Batata frita", "Porções", 19.90m),
            new("Onion rings", "Porções", 22.90m),
            new("Milk-shake", "Sobremesas", 18.90m),
            new("Refrigerante lata", "Bebidas", 7.00m),
            new("Suco natural", "Bebidas", 10.00m),
            new("Água mineral", "Bebidas", 4.00m)
        ]),
        new("Restaurante caseiro", [
            new("Prato feito de frango", "Pratos", 27.90m),
            new("Prato feito de carne", "Pratos", 32.90m),
            new("Prato feito de peixe", "Pratos", 34.90m),
            new("Parmegiana", "Pratos", 39.90m),
            new("Executivo vegetariano", "Pratos", 28.90m),
            new("Salada da casa", "Entradas", 19.90m),
            new("Arroz e feijão", "Acompanhamentos", 12.90m),
            new("Pudim", "Sobremesas", 10.00m),
            new("Refrigerante lata", "Bebidas", 7.00m),
            new("Suco natural", "Bebidas", 10.00m),
            new("Água mineral", "Bebidas", 4.00m),
            new("Café", "Bebidas", 5.00m)
        ])
    ];

    public RestaurantWindow(
        SqliteRestaurantRepository restaurant,
        SqliteStoreAccountRepository localAccounts,
        SqliteAccessControl accessControl,
        IProductRepository products,
        SqliteSyncOutboxRepository syncOutbox,
        StoreTypeSwitchService storeTypeSwitcher,
        StoreAccountIdentity storeAccount,
        string storeName)
    {
        InitializeComponent();
        _restaurant = restaurant;
        _kitchen = new KitchenPrintService(restaurant, accessControl);
        _localAccounts = localAccounts;
        _accessControl = accessControl;
        _products = products;
        _syncOutbox = syncOutbox;
        _storeTypeSwitcher = storeTypeSwitcher;
        _storeAccount = storeAccount;
        _storeName = storeName;
        MenuTemplateCombo.ItemsSource = MenuTemplates;
        MenuTemplateCombo.SelectedIndex = 0;
        StoreLabel.Text = storeName;
        StateChanged += (_, _) =>
        {
            MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
            MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "Restaurar" : "Maximizar";
        };
        Loaded += async (_, _) =>
        {
            try
            {
                await _restaurant.InitializeAsync();
                await LoadFeaturesAsync();
                await RefreshAreasAsync();
                await RefreshCashAsync();
                await RefreshSyncStatusAsync();
                var syncTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                syncTimer.Tick += async (_, _) =>
                {
                    await RefreshSyncStatusAsync();
                    await ProcessKitchenPrintAsync();
                    try
                    {
                        await RefreshTablesAsync();
                    }
                    catch (Exception exception)
                    {
                        AreaStatusText.Text = $"Falha ao atualizar as mesas: {exception.Message}";
                    }
                };
                syncTimer.Start();
                Closed += (_, _) => syncTimer.Stop();
                RestaurantOrderNotifier.OrderChanged += OnLocalOrderChanged;
                Closed += (_, _) => RestaurantOrderNotifier.OrderChanged -= OnLocalOrderChanged;
                await InitializeLocalServerAsync();
                Closed += (_, _) => _ = StopLocalServerAsync();
            }
            catch (Exception exception)
            {
                ShowError("Não foi possível iniciar o módulo de restaurante", exception);
            }
        };
    }

    private async Task ProcessKitchenPrintAsync()
    {
        try
        {
            await _kitchen.ProcessPendingAsync();
            var failed = await _kitchen.GetFailedAsync();
            var failedNotices = await _kitchen.GetFailedNoticesAsync();
            var pendingNotices = !_kitchen.Enabled ? await _restaurant.GetItemChangeNoticesAsync(failedOnly: false) : [];
            KitchenPrintAlert.Visibility = failed.Count == 0 && failedNotices.Count == 0 && pendingNotices.Count == 0
                ? Visibility.Collapsed : Visibility.Visible;
            if (failedNotices.Count > 0)
            {
                KitchenPrintAlertText.Text =
                    $"{failedNotices.Count} aviso(s) de alteração ou cancelamento não foram impressos. " +
                    $"Avise a cozinha ou o balcão e tente novamente. Erro: {failedNotices[0].Error}";
                if (failed.Count > 0)
                    KitchenPrintAlertText.Text += $" Também há itens de {failed.Count} mesa(s) com falha.";
            }
            else if (pendingNotices.Count > 0)
            {
                KitchenPrintAlertText.Text =
                    $"{pendingNotices.Count} aviso(s) aguardam impressão, mas a impressão automática está desativada. " +
                    "Avise a cozinha ou o balcão e habilite a impressão.";
            }
            else if (failed.Count > 0)
            {
                var items = failed.Sum(batch => batch.Items.Count);
                KitchenPrintAlertText.Text =
                    $"{items} item(ns) de {failed.Count} mesa(s) ({string.Join(", ", failed.Select(batch => "mesa " + batch.TableNumber))}) " +
                    $"não saíram na impressora. Erro: {failed[0].LastError}";
            }
        }
        catch (Exception exception)
        {
            FooterStatus.Text = $"Falha na impressão da cozinha: {exception.Message}";
        }
    }

    private async void KitchenRetry_Click(object sender, RoutedEventArgs e)
    {
        KitchenRetryButton.IsEnabled = false;
        try
        {
            await _kitchen.RetryFailedAsync();
            await ProcessKitchenPrintAsync();
        }
        finally
        {
            KitchenRetryButton.IsEnabled = true;
        }
    }

    private async Task RefreshSyncStatusAsync()
    {
        try
        {
            var status = await _syncOutbox.GetStatusAsync("restaurant");
            SyncHeadline.Text = status.PendingCount == 0
                ? "SEM PENDÊNCIAS"
                : $"{status.PendingCount} EVENTO(S) PENDENTE(S)";
            SyncDetail.Text = status.LastError is null
                ? "Sincronização Firebase automática"
                : status.LastError;
        }
        catch (Exception exception)
        {
            SyncHeadline.Text = "ESTADO INDISPONÍVEL";
            SyncDetail.Text = exception.Message;
        }
        await RefreshOfflineAlertAsync();
    }

    private DateTimeOffset _lastInternetCheck = DateTimeOffset.MinValue;
    private bool _lastInternetAvailable = true;
    private bool _internetCheckRunning;
    private static readonly System.Net.Http.HttpClient InternetCheckClient = new()
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    private Task RefreshOfflineAlertAsync()
    {
        // Roda a verificação em background para não travar o timer da UI.
        if (!_internetCheckRunning && DateTimeOffset.UtcNow - _lastInternetCheck >= TimeSpan.FromSeconds(10))
        {
            _internetCheckRunning = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    _lastInternetCheck = DateTimeOffset.UtcNow;
                    _lastInternetAvailable = await CheckInternetAsync();
                    DebugLog.Write($"[Offline] Verificação concluída: Internet={_lastInternetAvailable}");
                }
                catch (Exception exception)
                {
                    DebugLog.Write($"[Offline] Erro na verificação: {exception.Message}");
                    _lastInternetAvailable = false;
                }
                finally
                {
                    _internetCheckRunning = false;
                }
                await Dispatcher.BeginInvoke(new Action(UpdateOfflineAlertVisibility));
            });
        }
        UpdateOfflineAlertVisibility();
        return Task.CompletedTask;
    }

    private void UpdateOfflineAlertVisibility()
    {
        var serverRunning = _localServer is { IsRunning: true };
        // Mostra o alerta sempre que está sem internet; o texto muda se o servidor já está ativo.
        OfflineAlert.Visibility = !_lastInternetAvailable ? Visibility.Visible : Visibility.Collapsed;
        OfflineAlertText.Text = serverRunning
            ? "Sem internet. O servidor local está ativo — os garçons continuam pelo Wi-Fi da loja."
            : "Os garçons podem continuar atendendo pelo Wi-Fi da loja. Ative o servidor local para o webapp funcionar sem internet.";
        OfflineEnableServerButton.Visibility = serverRunning ? Visibility.Collapsed : Visibility.Visible;
        DebugLog.Write($"[Offline] Internet={_lastInternetAvailable}, Servidor={serverRunning}, Banner={OfflineAlert.Visibility}");
    }

    private static async Task<bool> CheckInternetAsync()
    {
        // Tenta primeiro uma conexão TCP rápida no DNS do Google (8.8.8.8:53) — falha rápido sem internet.
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            var connect = tcp.ConnectAsync("8.8.8.8", 53);
            var completed = await Task.WhenAny(connect, Task.Delay(2000));
            if (completed == connect && tcp.Connected)
                return true;
        }
        catch
        {
            // Cai para a verificação HTTP abaixo.
        }

        // Fallback: requisição HTTP leve (204 No Content).
        try
        {
            using var response = await InternetCheckClient.GetAsync("https://www.gstatic.com/generate_204");
            return response.IsSuccessStatusCode || (int)response.StatusCode == 204;
        }
        catch
        {
            return false;
        }
    }

    private async void OfflineEnableServer_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            OfflineEnableServerButton.IsEnabled = false;
            var port = int.TryParse(await _accessControl.GetSettingAsync("localServer.port"), out var savedPort)
                && savedPort is >= 1024 and <= 65535
                ? savedPort
                : 8080;
            await _accessControl.SetSettingAsync("localServer.enabled", "1");
            await _accessControl.SetSettingAsync("localServer.port", port.ToString(CultureInfo.InvariantCulture));
            await StartLocalServerAsync(port);
            LocalServerEnabledCheck.IsChecked = true;
            LocalServerPortBox.Text = port.ToString(CultureInfo.InvariantCulture);
            UpdateLocalServerStatus();
            UpdateOfflineAlertVisibility();
            var urls = LocalWaiterServer.GetLocalUrls(port);
            var lanUrl = urls.FirstOrDefault(url => !url.Contains("localhost"));

            if (lanUrl is null)
            {
                MessageBox.Show(
                    "Servidor local ativado, mas o computador não está conectado a nenhuma rede Wi-Fi/cabo.\n\n" +
                    "Ligue o roteador da loja e conecte este computador a ele. Depois vá em Configurações → Servidor local e clique em \"🖨 Imprimir QR Code\" para gerar o cartão dos garçons.",
                    "Sem rede local",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var answer = MessageBox.Show(
                $"Servidor local ativado!\n\nEndereço para os garçons (mesmo Wi-Fi):\n{lanUrl}\n\n" +
                "Deseja imprimir um QR Code na impressora do balcão para os garçons escanearem?\n\n" +
                "(Se for a primeira vez, clique em \"Liberar porta no Firewall\" em Configurações.)",
                "Servidor local ativo",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes)
                await PrintWaiterQrCodeAsync(lanUrl);
        }
        catch (Exception exception)
        {
            ShowError("Não foi possível ativar o servidor local", exception);
        }
        finally
        {
            OfflineEnableServerButton.IsEnabled = true;
        }
    }

    private async Task PrintWaiterQrCodeAsync(string url)
    {
        try
        {
            // Usa a impressora da cozinha como "impressora do balcão" (a principal da operação).
            var printer = await _accessControl.GetSettingAsync("printer.kitchen") ?? "Padrão do Windows";
            DebugLog.Write($"[QRCode] Iniciando impressão. url={url}, impressora={printer}");
            await KitchenTicketPrinter.PrintWaiterQrCodeAsync(url, _storeName, printer);
            FooterStatus.Text = "QR Code do app do garçom impresso.";
        }
        catch (Exception exception)
        {
            DebugLog.Write($"[QRCode] ERRO: {exception.Message}");
            ShowError("O servidor está ativo, mas não foi possível imprimir o QR Code", exception);
        }
    }

    private async void RestaurantFormatToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!_isSuperAdminAuthenticated)
        {
            RestaurantFormatToggle.IsChecked = true;
            MessageBox.Show("Entre como Super Admin em Configurações para alterar o formato da loja.",
                "Acesso restrito", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        const string newStoreType = "sales";
        if (MessageBox.Show(
                "Alterar o formato desta loja para Lojista / PDV?\n\nOs dados do Restaurante serão mantidos. O SYSMR será fechado para aplicar a mudança.",
                "Confirmar alteração de formato",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            RestaurantFormatToggle.IsChecked = true;
            return;
        }

        try
        {
            await _storeTypeSwitcher.ChangeAsync(_storeAccount, newStoreType);
            FooterStatus.Text = "Formato alterado para Lojista / PDV.";
            MessageBox.Show(
                "Formato alterado para Lojista / PDV. O SYSMR será fechado; abra o programa novamente para continuar.",
                "Formato atualizado",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Close();
        }
        catch (Exception exception)
        {
            RestaurantFormatToggle.IsChecked = true;
            ShowError("Não foi possível alterar o formato da loja", exception);
        }
    }

    private async Task RefreshAreasAsync(Guid? selectedAreaId = null)
    {
        _loadingAreas = true;
        var areas = await _restaurant.GetAreasAsync();
        var activeAreas = areas.Where(area => area.IsActive).ToArray();
        AreaCombo.ItemsSource = activeAreas;
        SettingsAreasGrid.ItemsSource = areas;
        var selected = activeAreas.FirstOrDefault(area => area.Id == selectedAreaId) ?? activeAreas.FirstOrDefault();
        AreaCombo.SelectedItem = selected;
        SettingsAreasGrid.SelectedItem =
            areas.FirstOrDefault(area => area.Id == selectedAreaId) ?? areas.FirstOrDefault();
        _loadingAreas = false;
        UpdateAreaActionButton();
        if (selected is not null)
            await RefreshTablesAsync();
        else
        {
            TablesItems.ItemsSource = Array.Empty<RestaurantTableRecord>();
            AreaStatusText.Text = "Nenhuma área ativa.";
        }
    }

    private async Task RefreshTablesAsync()
    {
        if (AreaCombo.SelectedItem is not RestaurantAreaRecord { IsActive: true } area)
            return;
        var tables = await _restaurant.GetTablesAsync(area.Id);
        TablesItems.ItemsSource = tables;
        AreaStatusText.Text = $"{tables.Count} mesas • {tables.Count(table => table.IsOccupied)} ocupadas";
    }

    private async Task RefreshMenuAsync() =>
        MenuGrid.ItemsSource = (await _restaurant.GetMenuItemsAsync())
            .Select(item => new MenuItemEditorRow(item))
            .ToArray();

    private async Task RefreshEmployeesAsync() =>
        EmployeesGrid.ItemsSource = await _restaurant.GetEmployeesAsync();

    private async Task LoadFeaturesAsync()
    {
        _dashboardEnabled = await _accessControl.GetSettingAsync("feature.dashboard") == "1";
        _expensesEnabled = await _accessControl.GetSettingAsync("feature.expenses") == "1";
        _waiterReportEnabled = await _accessControl.GetSettingAsync("feature.waiterReport") == "1";
        _csvEnabled = await _accessControl.GetSettingAsync("feature.csvExport") == "1";
        _lowStockEnabled = await _accessControl.GetSettingAsync("feature.lowStock") == "1";
        _blindCloseEnabled = await _accessControl.GetSettingAsync("feature.blindClose") == "1";
        _kitchenPrintEnabled = await _accessControl.GetSettingAsync("feature.autoKitchenPrint") != "0";
        _kitchen.Enabled = _kitchenPrintEnabled;
        _serviceChargeEnabled = await _accessControl.GetSettingAsync("feature.serviceCharge") != "0";
        _commissionPercent = decimal.TryParse(await _accessControl.GetSettingAsync("waiter.commissionPercent"),
            NumberStyles.Number, CultureInfo.InvariantCulture, out var commission) && commission is >= 0 and <= 100
            ? commission
            : 10m;
        FeatureDashboardCheck.IsChecked = _dashboardEnabled;
        FeatureExpensesCheck.IsChecked = _expensesEnabled;
        FeatureWaiterReportCheck.IsChecked = _waiterReportEnabled;
        FeatureCsvCheck.IsChecked = _csvEnabled;
        FeatureLowStockCheck.IsChecked = _lowStockEnabled;
        FeatureBlindCloseCheck.IsChecked = _blindCloseEnabled;
        FeatureKitchenPrintCheck.IsChecked = _kitchenPrintEnabled;
        FeatureServiceChargeCheck.IsChecked = _serviceChargeEnabled;
        CommissionBox.Text = _commissionPercent.ToString("0.##", CultureInfo.CurrentCulture);
        await LoadPrintersAsync();
        DashboardPanel.Visibility = _dashboardEnabled || _expensesEnabled ? Visibility.Visible : Visibility.Collapsed;
        DashboardWaiterPanel.Visibility = _waiterReportEnabled ? Visibility.Visible : Visibility.Collapsed;
        ExportCsvButton.Visibility = _csvEnabled ? Visibility.Visible : Visibility.Collapsed;
        ExpensesPanel.Visibility = _expensesEnabled ? Visibility.Visible : Visibility.Collapsed;
        DashboardExpensesKpi.Visibility = _expensesEnabled ? Visibility.Visible : Visibility.Collapsed;
        DashboardResultKpi.Visibility = _expensesEnabled ? Visibility.Visible : Visibility.Collapsed;
        DashboardExpenseCategoriesPanel.Visibility = _expensesEnabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Feature_Click(object sender, RoutedEventArgs e)
    {
        if (!_isSuperAdminAuthenticated)
            return;
        try
        {
            await SetFeatureAsync("feature.dashboard", FeatureDashboardCheck);
            await SetFeatureAsync("feature.expenses", FeatureExpensesCheck);
            await SetFeatureAsync("feature.waiterReport", FeatureWaiterReportCheck);
            await SetFeatureAsync("feature.csvExport", FeatureCsvCheck);
            await SetFeatureAsync("feature.lowStock", FeatureLowStockCheck);
            await SetFeatureAsync("feature.blindClose", FeatureBlindCloseCheck);
            if (FeatureKitchenPrintCheck.IsChecked == true && !_kitchenPrintEnabled)
                await _restaurant.MarkAllKitchenItemsPrintedAsync();
            await SetFeatureAsync("feature.autoKitchenPrint", FeatureKitchenPrintCheck);
            await SetFeatureAsync("feature.serviceCharge", FeatureServiceChargeCheck);
            await LoadFeaturesAsync();
            await RefreshCashAsync();
            FooterStatus.Text = "Recursos da loja atualizados.";
        }
        catch (Exception exception)
        {
            ShowError("Não foi possível salvar os recursos", exception);
        }
    }

    private Task SetFeatureAsync(string key, CheckBox box) =>
        _accessControl.SetSettingAsync(key, box.IsChecked == true ? "1" : "0");

    private async Task LoadPrintersAsync()
    {
        if (!IsLoaded || KitchenPrinterCombo is null || BarPrinterCombo is null)
            return;
        try
        {
            var printers = new List<string> { "Padrão do Windows" };
            foreach (var printer in System.Drawing.Printing.PrinterSettings.InstalledPrinters)
                printers.Add(printer.ToString()!);

            KitchenPrinterCombo.ItemsSource = printers;
            BarPrinterCombo.ItemsSource = printers;

            var kitchenPrinter = await _accessControl.GetSettingAsync("printer.kitchen") ?? "Padrão do Windows";
            var barPrinter = await _accessControl.GetSettingAsync("printer.bar") ?? "Padrão do Windows";

            KitchenPrinterCombo.Text = kitchenPrinter;
            BarPrinterCombo.Text = barPrinter;
        }
        catch (Exception exception)
        {
            FooterStatus.Text = $"Falha ao carregar impressoras: {exception.Message}";
        }
    }

    private async void RefreshPrinters_Click(object sender, RoutedEventArgs e)
    {
        await LoadPrintersAsync();
        FooterStatus.Text = "Lista de impressoras atualizada.";
    }

    private async void SavePrinters_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _accessControl.SetSettingAsync("printer.kitchen", NormalizePrinterTarget(KitchenPrinterCombo.Text));
            await _accessControl.SetSettingAsync("printer.bar", NormalizePrinterTarget(BarPrinterCombo.Text));
            FooterStatus.Text = "Impressoras salvas.";
        }
        catch (Exception exception)
        {
            ShowError("Não foi possível salvar as impressoras", exception);
        }
    }

    private static string NormalizePrinterTarget(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "Padrão do Windows" : value.Trim();

    private async Task InitializeLocalServerAsync()
    {
        try
        {
            var enabled = await _accessControl.GetSettingAsync("localServer.enabled") == "1";
            var port = int.TryParse(await _accessControl.GetSettingAsync("localServer.port"), out var savedPort)
                && savedPort is >= 1024 and <= 65535
                ? savedPort
                : 8080;
            LocalServerEnabledCheck.IsChecked = enabled;
            LocalServerPortBox.Text = port.ToString(CultureInfo.InvariantCulture);
            DebugLog.Write($"[LocalServer] Inicializando: enabled={enabled}, port={port}");
            if (enabled)
                await StartLocalServerAsync(port);
            UpdateLocalServerStatus();
        }
        catch (Exception exception)
        {
            DebugLog.Write($"[LocalServer] ERRO ao iniciar: {exception.Message}");
            FooterStatus.Text = $"Falha ao iniciar o servidor local: {exception.Message}";
        }
    }

    private async Task StartLocalServerAsync(int port)
    {
        await StopLocalServerAsync();
        DebugLog.Write($"[LocalServer] Iniciando na porta {port}...");
        var server = new LocalWaiterServer(_restaurant, _storeName, port);
        await server.StartAsync();
        _localServer = server;
        DebugLog.Write($"[LocalServer] Servidor iniciado. IsRunning={server.IsRunning}");
    }

    private async Task StopLocalServerAsync()
    {
        if (_localServer is null)
            return;
        DebugLog.Write("[LocalServer] Parando servidor...");
        await _localServer.DisposeAsync();
        _localServer = null;
    }

    private void OnLocalOrderChanged(object? sender, EventArgs e)
    {
        DebugLog.Write("[RestaurantWindow] OrderChanged recebido!");
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            DebugLog.Write("[RestaurantWindow] Processando impressão e atualizando mesas...");
            await ProcessKitchenPrintAsync();
            try
            {
                await RefreshTablesAsync();
                DebugLog.Write("[RestaurantWindow] Mesas atualizadas.");
            }
            catch (Exception ex)
            {
                DebugLog.Write($"[RestaurantWindow] Erro ao atualizar mesas: {ex.Message}");
            }
        }));
    }

    private void UpdateLocalServerStatus()
    {
        if (_localServer is { IsRunning: true })
        {
            LocalServerStatusText.Text = "Servidor ativo. O garçom conecta no Wi-Fi da loja e abre um dos endereços:";
            LocalServerUrlText.Text = string.Join("   •   ", LocalWaiterServer.GetLocalUrls(_localServer.Port));
            return;
        }
        LocalServerStatusText.Text = "Servidor desativado.";
        LocalServerUrlText.Text = string.Empty;
    }

    private async void SaveLocalServer_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var enabled = LocalServerEnabledCheck.IsChecked == true;
            if (!int.TryParse(LocalServerPortBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
                || port is < 1024 or > 65535)
            {
                MessageBox.Show("Informe uma porta entre 1024 e 65535.", "Porta inválida",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                LocalServerPortBox.Text = "8080";
                return;
            }

            await _accessControl.SetSettingAsync("localServer.enabled", enabled ? "1" : "0");
            await _accessControl.SetSettingAsync("localServer.port", port.ToString(CultureInfo.InvariantCulture));
            if (enabled)
                await StartLocalServerAsync(port);
            else
                await StopLocalServerAsync();
            UpdateLocalServerStatus();
            FooterStatus.Text = enabled
                ? "Servidor local ativo. Teste abrindo o endereço no celular."
                : "Servidor local desativado.";
        }
        catch (Exception exception)
        {
            ShowError("Não foi possível aplicar o servidor local", exception);
            UpdateLocalServerStatus();
        }
    }

    private async void PrintQr_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_localServer is not { IsRunning: true })
            {
                MessageBox.Show("Ative o servidor local primeiro para gerar o QR Code com o endereço correto.",
                    "Servidor desligado", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var urls = LocalWaiterServer.GetLocalUrls(_localServer.Port);
            var lanUrl = urls.FirstOrDefault(url => !url.Contains("localhost"));
            if (lanUrl is null)
            {
                MessageBox.Show(
                    "O computador não está conectado a nenhuma rede Wi-Fi/cabo.\n\n" +
                    "Ligue o roteador da loja, conecte este computador a ele e tente novamente.",
                    "Sem rede local",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
            await PrintWaiterQrCodeAsync(lanUrl);
        }
        catch (Exception exception)
        {
            ShowError("Não foi possível imprimir o QR Code", exception);
        }
    }

    private void Firewall_Click(object sender, RoutedEventArgs e)
    {
        var port = _localServer?.Port
            ?? (int.TryParse(LocalServerPortBox.Text, out var savedPort) ? savedPort : 8080);
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"advfirewall firewall add rule name=\"SYSMR Garçons (porta {port})\" dir=in action=allow protocol=TCP localport={port}",
                Verb = "runas",
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(startInfo)?.WaitForExit();
            MessageBox.Show(
                "Regra de firewall criada. Se o celular ainda não conectar, verifique se ele está no mesmo Wi-Fi do computador.",
                "Firewall", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(
                "A liberação foi cancelada. Ela exige permissão de administrador do Windows.",
                "Firewall", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void CommissionBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!RestaurantEntryWindow.TryReadAmount(CommissionBox.Text, out var percent) || percent is < 0 or > 100)
        {
            CommissionBox.Text = _commissionPercent.ToString("0.##", CultureInfo.CurrentCulture);
            return;
        }
        if (percent == _commissionPercent)
            return;
        try
        {
            await _accessControl.SetSettingAsync("waiter.commissionPercent", percent.ToString(CultureInfo.InvariantCulture));
            _commissionPercent = percent;
            await RefreshDashboardAsync();
        }
        catch (Exception exception)
        {
            ShowError("Não foi possível salvar a comissão", exception);
        }
    }

    private async Task RefreshLowStockAsync()
    {
        if (!_lowStockEnabled)
        {
            LowStockPanel.Visibility = Visibility.Collapsed;
            return;
        }
        var low = (await _products.SearchLowStockAsync(decimal.MaxValue))
            .Where(product => product.MinimumStock > 0 && product.Stock <= product.MinimumStock)
            .Select(product => $"{product.Name}: {product.Stock:0.##} em estoque (mínimo {product.MinimumStock:0.##})")
            .ToArray();
        LowStockList.ItemsSource = low;
        LowStockPanel.Visibility = low.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_lastDashboardRange is not { } range)
            return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv",
            FileName = $"sysmr-dashboard-{range.From:yyyyMMdd}-{range.To:yyyyMMdd}.csv"
        };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            var data = await _restaurant.GetDashboardAsync(range.From, range.To);
            var pt = CultureInfo.GetCultureInfo("pt-BR");
            var csv = new System.Text.StringBuilder();
            void Line(params string[] cells) => csv.AppendLine(string.Join(';', cells.Select(CsvCell)));
            Line("Período", $"{range.From:dd/MM/yyyy} a {range.To:dd/MM/yyyy}");
            Line("Faturamento", data.TotalRevenue.ToString("N2", pt));
            Line("Comandas pagas", data.OrderCount.ToString(pt));
            Line("Ticket médio", data.AverageTicket.ToString("N2", pt));
            if (_expensesEnabled)
            {
                Line("Gastos", data.TotalExpenses.ToString("N2", pt));
                Line("Resultado", (data.TotalRevenue - data.TotalExpenses).ToString("N2", pt));
            }
            csv.AppendLine();
            Line("Dia", "Total");
            foreach (var day in data.Days)
                Line(day.Date.ToString("dd/MM/yyyy"), day.Total.ToString("N2", pt));
            csv.AppendLine();
            Line("Forma de pagamento", "Total");
            foreach (var method in data.Methods)
                Line(PaymentLabel(method.PaymentMethod), method.Total.ToString("N2", pt));
            if (_expensesEnabled)
            {
                csv.AppendLine();
                Line("Categoria de gasto", "Total");
                foreach (var category in data.ExpenseCategories ?? [])
                    Line(category.Category, category.Total.ToString("N2", pt));
            }
            if (_waiterReportEnabled)
            {
                csv.AppendLine();
                Line("Garçom", "Comandas", "Vendas", "Comissão");
                foreach (var sales in await _restaurant.GetWaiterSalesAsync(range.From, range.To))
                    Line(sales.Name, sales.Orders.ToString(pt), sales.Total.ToString("N2", pt),
                        (sales.Total * _commissionPercent / 100m).ToString("N2", pt));
            }
            await System.IO.File.WriteAllTextAsync(dialog.FileName, csv.ToString(), new System.Text.UTF8Encoding(true));
            FooterStatus.Text = "Dashboard exportado para CSV.";
        }
        catch (Exception exception)
        {
            ShowError("Não foi possível exportar o CSV", exception);
        }
    }

    private static string CsvCell(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' && !decimal.TryParse(value, NumberStyles.Any, CultureInfo.GetCultureInfo("pt-BR"), out _))
            value = "'" + value;
        return value.IndexOfAny([';', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    private async Task RefreshExpensesAsync(DateOnly from, DateOnly to) =>
        ExpensesGrid.ItemsSource = await _restaurant.GetExpensesAsync(from, to);

    private async void AddExpense_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new RestaurantEntryWindow(
            "Registrar gasto",
            ["Descrição", "Categoria (ex.: Fornecedor, Gás, Aluguel)", "Valor", "Pago com dinheiro do caixa? (S/N)"]) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;
        if (!RestaurantEntryWindow.TryReadAmount(dialog.Values[2], out var amount))
        {
            MessageBox.Show(this, "Informe um valor válido.", "Gasto", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var fromCash = dialog.Values[3].Trim().StartsWith("S", StringComparison.OrdinalIgnoreCase);
        try
        {
            await _restaurant.AddExpenseAsync(dialog.Values[0], dialog.Values[1], amount, fromCash);
            await RefreshCashAsync();
            FooterStatus.Text = $"Gasto de {amount:C2} registrado.";
        }
        catch (Exception exception)
        {
            ShowError("Não foi possível registrar o gasto", exception);
        }
    }

    private async void DeleteExpense_Click(object sender, RoutedEventArgs e)
    {
        if (ExpensesGrid.SelectedItem is not RestaurantExpenseRecord expense)
        {
            MessageBox.Show(this, "Selecione um gasto na lista.", "Gasto", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this, $"Excluir o gasto \"{expense.Description}\" de {expense.Amount:C2}?", "Excluir gasto",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try
        {
            await _restaurant.DeleteExpenseAsync(expense.Id);
            await RefreshCashAsync();
        }
        catch (Exception exception)
        {
            ShowError("Não foi possível excluir o gasto", exception);
        }
    }

    private async Task RefreshDashboardAsync()
    {
        if (!_dashboardEnabled && !_expensesEnabled)
            return;
        if (DashboardFromPicker.SelectedDate is not { } from || DashboardToPicker.SelectedDate is not { } to)
            return;
        if (to < from)
        {
            MessageBox.Show(this, "A data final não pode ser anterior à inicial.", "Dashboard", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if ((to - from).TotalDays > 366)
        {
            MessageBox.Show(this, "Escolha um intervalo de até 1 ano.", "Dashboard", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            var data = await _restaurant.GetDashboardAsync(DateOnly.FromDateTime(from), DateOnly.FromDateTime(to));
            _lastDashboardRange = (data.From, data.To);
            if (_waiterReportEnabled)
            {
                var waiters = await _restaurant.GetWaiterSalesAsync(data.From, data.To);
                var maxWaiter = waiters.Count == 0 ? 0m : waiters.Max(waiter => waiter.Total);
                DashboardWaitersList.ItemsSource = waiters.Select(waiter => new DashboardBar(
                    $"{waiter.Name} ({waiter.Orders} comanda(s) • comissão {(waiter.Total * _commissionPercent / 100m):C2})",
                    waiter.Total, maxWaiter)).ToArray();
            }
            DashboardRevenueText.Text = data.TotalRevenue.ToString("C2");
            DashboardOrdersText.Text = data.OrderCount.ToString(CultureInfo.CurrentCulture);
            DashboardTicketText.Text = data.AverageTicket.ToString("C2");
            DashboardExpensesText.Text = data.TotalExpenses.ToString("C2");
            var result = data.TotalRevenue - data.TotalExpenses;
            DashboardResultText.Text = result.ToString("C2");
            DashboardResultText.Foreground = result < 0 ? System.Windows.Media.Brushes.Firebrick : System.Windows.Media.Brushes.ForestGreen;
            var categories = data.ExpenseCategories ?? [];
            var maxCategory = categories.Count == 0 ? 0m : categories.Max(category => category.Total);
            DashboardCategoriesList.ItemsSource = categories.Select(category => new DashboardBar(
                category.Category, category.Total, maxCategory)).ToArray();
            if (_expensesEnabled)
                await RefreshExpensesAsync(data.From, data.To);
            var maxDay = data.Days.Count == 0 ? 0m : data.Days.Max(day => day.Total);
            DashboardDaysList.ItemsSource = data.Days.Select(day => new DashboardBar(
                day.Date.ToString("dd/MM"), day.Total, maxDay)).ToArray();
            var maxMethod = data.Methods.Count == 0 ? 0m : data.Methods.Max(method => method.Total);
            DashboardMethodsList.ItemsSource = data.Methods.Select(method => new DashboardBar(
                PaymentLabel(method.PaymentMethod), method.Total, maxMethod)).ToArray();
        }
        catch (Exception exception)
        {
            ShowError("Não foi possível carregar o dashboard", exception);
        }
    }

    private async void ApplyDashboard_Click(object sender, RoutedEventArgs e) => await RefreshDashboardAsync();

    private async void DashboardPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string days } || !int.TryParse(days, out var back))
            return;
        DashboardToPicker.SelectedDate = DateTime.Today;
        DashboardFromPicker.SelectedDate = DateTime.Today.AddDays(-back);
        await RefreshDashboardAsync();
    }

    private static string PaymentLabel(PaymentMethod method) => method switch
    {
        PaymentMethod.Cash => "Dinheiro",
        PaymentMethod.Pix => "Pix",
        PaymentMethod.DebitCard => "Cartão de débito",
        PaymentMethod.CreditCard => "Cartão de crédito",
        _ => method.ToString()
    };

    private sealed record DashboardBar(string Label, decimal Total, decimal Max)
    {
        public double Percent => Max <= 0 ? 0 : (double)(Total / Max * 100m);
        public string TotalText => Total.ToString("C2");
    }

    private async Task RefreshCashAsync()
    {
        await LoadFeaturesAsync();
        if (DashboardFromPicker.SelectedDate is null || DashboardToPicker.SelectedDate is null)
        {
            DashboardToPicker.SelectedDate = DateTime.Today;
            DashboardFromPicker.SelectedDate = DateTime.Today.AddDays(-6);
        }
        await RefreshDashboardAsync();
        var session = await _restaurant.GetOpenCashSessionAsync();
        var isOpen = session is not null;
        OpenCashButton.IsEnabled = !isOpen;
        CloseCashButton.IsEnabled = isOpen;
        CashStatusText.Text = isOpen ? "Caixa aberto" : "Caixa fechado";
        CashOpeningText.Text = isOpen
            ? $"Abertura: {session!.OpenedAt.ToLocalTime():dd/MM/yyyy HH:mm} • Fundo inicial: {session.OpeningAmount:C2}"
            : "Abra o caixa para registrar pagamentos de comandas.";
        CashSalesText.Text = isOpen
            ? $"Recebimentos em dinheiro: {session!.CashSales:C2} • outras formas: {session.OtherSales:C2}" +
              (session.CashExpenses > 0 ? $" • gastos em dinheiro: {session.CashExpenses:C2}" : string.Empty)
            : string.Empty;
        CashExpectedText.Text = isOpen && !_blindCloseEnabled
            ? $"Dinheiro esperado em caixa: {session!.OpeningAmount + session.CashSales - session.CashExpenses:C2}"
            : isOpen ? "Fechamento cego: o valor esperado só aparece após a contagem." : string.Empty;
        await RefreshLowStockAsync();
    }

    private async void Area_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingAreas && IsLoaded)
            await RefreshTablesAsync();
    }

    private async void RefreshTables_Click(object sender, RoutedEventArgs e) => await RefreshTablesAsync();

    private async void Table_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RestaurantTableRecord table })
            return;
        try
        {
            var employees = (await _restaurant.GetEmployeesAsync())
                .Where(employee => employee.IsActive)
                .ToArray();
            var orderWindow = new RestaurantOrderWindow(_restaurant, table, employees, _serviceChargeEnabled, _kitchen) { Owner = this };
            orderWindow.ShowDialog();
            await RefreshTablesAsync();
            await RefreshCashAsync();
        }
        catch (Exception exception)
        {
            ShowError("Não foi possível abrir a mesa", exception);
        }
    }

    private void ShowTables_Click(object sender, RoutedEventArgs e) => ShowPage("Tables");

    private async void ShowMenu_Click(object sender, RoutedEventArgs e)
    {
        ShowPage("Menu");
        await RefreshMenuAsync();
    }

    private async void ShowEmployees_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RefreshEmployeesAsync();
            ShowPage("Employees");
        }
        catch (Exception exception)
        {
            ShowError("Não foi possível carregar a lista de funcionários", exception);
        }
    }

    private async void ShowCash_Click(object sender, RoutedEventArgs e)
    {
        ShowPage("Cash");
        await RefreshCashAsync();
    }

    private async void ShowSettings_Click(object sender, RoutedEventArgs e)
    {
        ShowPage("Settings");
        await RefreshAreasAsync((AreaCombo.SelectedItem as RestaurantAreaRecord)?.Id);
        await LoadPrintersAsync();
    }

    private void ShowPage(string target)
    {
        TablesPage.Visibility = target == "Tables" ? Visibility.Visible : Visibility.Collapsed;
        MenuPage.Visibility = target == "Menu" ? Visibility.Visible : Visibility.Collapsed;
        EmployeesPage.Visibility = target == "Employees" ? Visibility.Visible : Visibility.Collapsed;
        CashPage.Visibility = target == "Cash" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = target == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        PageHeading.Text = target switch
        {
            "Menu" => "Cardápio",
            "Employees" => "Funcionários",
            "Cash" => "Caixa",
            "Settings" => "Configurações do restaurante",
            _ => "Salão e mesas"
        };
        PageSubtitle.Text = target switch
        {
            "Menu" => "Gerencie pratos, porções e bebidas disponíveis para as comandas.",
            "Employees" => "Cadastre as pessoas que atendem as mesas e acompanhe o status.",
            "Cash" => "Acompanhe a abertura, os recebimentos e o fechamento do caixa.",
            "Settings" => "Organize as áreas, as mesas e os acessos administrativos da loja.",
            _ => "Mesas cinzas estão livres; mesas verdes têm comanda aberta."
        };
    }

    private void AdminSession_Click(object sender, RoutedEventArgs e)
    {
        if (_isSuperAdminAuthenticated)
        {
            _isSuperAdminAuthenticated = false;
            AdminToolsPanel.Visibility = Visibility.Collapsed;
            AdminSessionStatus.Text = "Acesso administrativo bloqueado.";
            AdminSessionButton.Content = "Entrar como Super Admin";
            return;
        }

        var authorization = new AdminPinWindow(_accessControl, initialSetup: false) { Owner = this };
        if (authorization.ShowDialog() != true)
            return;

        _isSuperAdminAuthenticated = true;
        AdminToolsPanel.Visibility = Visibility.Visible;
        AdminSessionStatus.Text = "Super Admin autenticado nesta sessão.";
        AdminSessionButton.Content = "Sair do modo Super Admin";
    }

    private async void NewArea_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new RestaurantEntryWindow("Criar área", ["Nome da área", "Quantidade de mesas"]) { Owner = this };
        if (dialog.ShowDialog() != true ||
            !int.TryParse(dialog.Values[1], NumberStyles.Integer, CultureInfo.CurrentCulture, out var tableCount) ||
            tableCount is < 1 or > 200)
        {
            if (dialog.DialogResult == true)
                MessageBox.Show("Informe uma quantidade de mesas entre 1 e 200.", "Quantidade inválida",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var areaId = await _restaurant.SaveAreaAsync(dialog.Values[0]);
            await _restaurant.SetTableCountAsync(areaId, tableCount);
            await RefreshAreasAsync(areaId);
            FooterStatus.Text = $"Área {dialog.Values[0]} criada com {tableCount} mesas.";
        }
        catch (Exception exception)
        {
            ShowError("Não foi possível criar a área", exception);
        }
    }

    private async void SetTableCount_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsAreasGrid.SelectedItem is not RestaurantAreaRecord area)
        {
            FooterStatus.Text = "Selecione uma área para configurar suas mesas.";
            return;
        }
        var dialog = new RestaurantEntryWindow(
            $"Mesas • {area.Name}", ["Quantidade de mesas"]) { Owner = this };
        if (dialog.ShowDialog() != true ||
            !int.TryParse(dialog.Values[0], NumberStyles.Integer, CultureInfo.CurrentCulture, out var tableCount) ||
            tableCount is < 1 or > 200)
        {
            if (dialog.DialogResult == true)
                MessageBox.Show("Informe uma quantidade de mesas entre 1 e 200.", "Quantidade inválida",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            await _restaurant.SetTableCountAsync(area.Id, tableCount);
            await RefreshAreasAsync(area.Id);
            FooterStatus.Text = $"A área {area.Name} agora tem {tableCount} mesas.";
        }
        catch (Exception exception)
        {
            ShowError("Não foi possível configurar a quantidade de mesas", exception);
        }
    }

    private async void ToggleArea_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsAreasGrid.SelectedItem is not RestaurantAreaRecord area)
        {
            FooterStatus.Text = "Selecione uma área para ativar ou desativar.";
            return;
        }

        var newIsActive = !area.IsActive;
        var action = newIsActive ? "reativar" : "desativar";
        var status = newIsActive ? "reativada" : "desativada";
        var confirmation = newIsActive
            ? $"Reativar a área “{area.Name}” e suas mesas?"
            : $"Desativar a área “{area.Name}”?\n\nA área e suas mesas serão ocultadas do webapp e continuarão no histórico. Áreas com comandas abertas não podem ser desativadas.";
        if (MessageBox.Show(
                confirmation,
                "Confirmar alteração da área",
                MessageBoxButton.YesNo,
                newIsActive ? MessageBoxImage.Question : MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        try
        {
            await _restaurant.SetAreaActiveAsync(area.Id, newIsActive);
            await RefreshAreasAsync(area.Id);
            FooterStatus.Text = $"Área {area.Name} {status}.";
        }
        catch (Exception exception)
        {
            ShowError($"Não foi possível {action} a área", exception);
        }
    }

    private void SettingsAreasGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateAreaActionButton();

    private void UpdateAreaActionButton()
    {
        if (ToggleAreaButton is not null)
            ToggleAreaButton.Content =
                (SettingsAreasGrid.SelectedItem as RestaurantAreaRecord)?.IsActive == true
                    ? "Desativar área"
                    : "Reativar área";
    }

    private async void NewMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await SaveMenuItemAsync(null);
    }

    private async void ReplacePesqueiroMenu_Click(object sender, RoutedEventArgs e)
    {
        if (!_isSuperAdminAuthenticated)
        {
            MessageBox.Show("Entre como Super Admin para substituir o cardápio.",
                "Acesso restrito", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MessageBox.Show(
                "Substituir o cardápio ativo pelo cardápio do Pesqueiro Mendonça?\n\nOs itens antigos serão desativados e os vínculos de estoque dos itens coincidentes serão removidos. O histórico será preservado. Feche todas as comandas antes de continuar.",
                "Confirmar substituição", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        try
        {
            await _restaurant.ReplaceWithPesqueiroMenuAsync();
            await RefreshMenuAsync();
            FooterStatus.Text = "Cardápio do pesqueiro instalado. Confira os preços e a sincronização.";
        }
        catch (Exception exception)
        {
            ShowError("Não foi possível substituir o cardápio", exception);
        }
    }

    private async void UpdatePesqueiroDescriptions_Click(object sender, RoutedEventArgs e)
    {
        if (!_isSuperAdminAuthenticated)
        {
            MessageBox.Show("Entre como Super Admin para atualizar as descrições.",
                "Acesso restrito", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MessageBox.Show(
                "Preencher as descrições vazias dos itens ativos do cardápio do pesqueiro?\n\nAs descrições já preenchidas, preços, opções e estoque serão preservados.",
                "Atualizar descrições", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        try
        {
            var count = await _restaurant.UpdatePesqueiroDescriptionsAsync();
            await RefreshMenuAsync();
            MessageBox.Show(count == 0
                    ? "Nenhuma descrição vazia correspondente foi encontrada."
                    : $"{count} descrição(ões) preenchida(s). A sincronização publicará as alterações.",
                "Descrições do cardápio", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            ShowError("Não foi possível atualizar as descrições", exception);
        }
    }

    private async void ApplyMenuTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (!_isSuperAdminAuthenticated)
        {
            MessageBox.Show("Entre como Super Admin para adicionar um modelo de cardápio.",
                "Acesso restrito", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MenuTemplateCombo.SelectedItem is not MenuTemplateOption template)
        {
            MessageBox.Show("Selecione um tipo de restaurante.", "Modelo necessário",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var existingNames = (await _restaurant.GetMenuItemsAsync())
                .Select(item => item.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missingItems = template.Items.Where(item => !existingNames.Contains(item.Name)).ToArray();
            if (missingItems.Length == 0)
            {
                MessageBox.Show($"Todos os itens sugeridos para {template.Name} já estão no cardápio.",
                    "Cardápio atualizado", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (MessageBox.Show(
                    $"Adicionar {missingItems.Length} itens sugeridos para {template.Name}?\n\nOs preços são exemplos e devem ser revisados. Itens com nomes já cadastrados serão preservados.",
                    $"Confirmar modelo • {template.Name}",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Information) != MessageBoxResult.OK)
                return;

            foreach (var item in missingItems)
            {
                await _restaurant.SaveMenuItemAsync(new RestaurantMenuItemRecord(
                    Guid.NewGuid(), item.Name, item.Category, item.Price, true));
            }

            await RefreshMenuAsync();
            FooterStatus.Text = $"Modelo {template.Name} adicionado: {missingItems.Length} itens para revisar.";
        }
        catch (Exception exception)
        {
            ShowError("Não foi possível adicionar o modelo de cardápio", exception);
        }
    }

    private async void EditMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (MenuGrid.SelectedItem is not MenuItemEditorRow selected)
        {
            FooterStatus.Text = "Selecione um item do cardápio para editar.";
            return;
        }
        await SaveMenuItemAsync(selected.ToRecord());
    }

    private async void EditMenuOptions_Click(object sender, RoutedEventArgs e)
    {
        if (MenuGrid.SelectedItem is not MenuItemEditorRow selected)
        {
            FooterStatus.Text = "Selecione um item do cardápio para editar as variações.";
            return;
        }
        var current = (await _restaurant.GetMenuItemsAsync()).FirstOrDefault(item => item.Id == selected.Id)?.Options ?? [];
        var dialog = new MenuOptionsWindow(selected.Name, current) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            await _restaurant.SaveMenuItemAsync(selected.ToRecord() with { Options = dialog.Options });
            await RefreshMenuAsync();
            FooterStatus.Text = "Variações salvas.";
        }
        catch (Exception exception)
        {
            ShowError("Não foi possível salvar as variações", exception);
        }
    }

    private async Task SaveMenuItemAsync(RestaurantMenuItemRecord? existing)
    {
        var dialog = new RestaurantEntryWindow(
            existing is null ? "Novo item do cardápio" : "Editar item do cardápio",
            ["Nome do item", "Categoria", "Preço", "Código de barras do estoque (opcional)", "Quantidade consumida por unidade (opcional)"],
            optionalFieldIndexes: new HashSet<int> { 3, 4 }) { Owner = this };
        var linkedProduct = existing?.InventoryProductId is Guid productId
            ? await _products.FindByIdAsync(productId)
            : null;
        if (existing is not null)
        {
            dialog.Loaded += (_, _) =>
            {
                dialog.ValueOne.Text = existing.Name;
                dialog.ValueTwo.Text = existing.Category;
                dialog.ValueThree.Text = existing.Price.ToString(CultureInfo.CurrentCulture);
                dialog.ValueFour.Text = linkedProduct?.Barcode ?? string.Empty;
                dialog.ValueFive.Text = existing.InventoryQuantityPerUnit.ToString(CultureInfo.CurrentCulture);
            };
        }
        if (dialog.ShowDialog() != true ||
            !RestaurantEntryWindow.TryReadAmount(dialog.Values[2], out var price) ||
            price <= 0)
        {
            if (dialog.DialogResult == true)
                MessageBox.Show("Informe um preço maior que zero.", "Preço inválido",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Guid? inventoryProductId = null;
        var inventoryQuantity = 0m;
        if (!string.IsNullOrWhiteSpace(dialog.Values[3]))
        {
            var product = await _products.FindByBarcodeAsync(dialog.Values[3])
                          ?? await _products.FindByScaleCodeAsync(dialog.Values[3]);
            if (product is null)
            {
                MessageBox.Show("Nenhum produto cadastrado possui esse código de barras.", "Produto não encontrado",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!RestaurantEntryWindow.TryReadAmount(dialog.Values[4], out inventoryQuantity) || inventoryQuantity <= 0)
            {
                MessageBox.Show("Informe a quantidade retirada do estoque por unidade do cardápio.", "Quantidade inválida",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            inventoryProductId = product.Id;
        }
        try
        {
            await _restaurant.SaveMenuItemAsync(new RestaurantMenuItemRecord(
                existing?.Id ?? Guid.NewGuid(),
                dialog.Values[0],
                dialog.Values[1],
                price,
                existing?.IsActive ?? true,
                inventoryProductId,
                inventoryQuantity,
                PrintTarget: existing?.PrintTarget ?? "kitchen",
                PriceM: existing?.PriceM,
                PriceG: existing?.PriceG,
                Description: existing?.Description ?? ""));
            await RefreshMenuAsync();
            FooterStatus.Text = "Cardápio salvo.";
        }
        catch (Exception exception)
        {
            ShowError("Não foi possível salvar o item do cardápio", exception);
        }
    }

    private async void ToggleMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (MenuGrid.SelectedItem is not MenuItemEditorRow item)
            return;
        try
        {
            await _restaurant.SaveMenuItemAsync(item.ToRecord() with { IsActive = !item.IsActive });
            await RefreshMenuAsync();
        }
        catch (Exception exception)
        {
            ShowError("Não foi possível atualizar o item", exception);
        }
    }

    private void MenuGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ToggleMenuButton is not null)
            ToggleMenuButton.ToolTip = (MenuGrid.SelectedItem as MenuItemEditorRow)?.IsActive == true
                ? "Desativar item"
                : "Ativar item";
    }

    private void MenuGrid_RowEditEnding(object sender, DataGridRowEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || e.Row.Item is not MenuItemEditorRow item)
            return;
        Dispatcher.BeginInvoke(new Action(async () => await SaveInlineMenuItemAsync(item)));
    }

    private async Task SaveInlineMenuItemAsync(MenuItemEditorRow item)
    {
        var name = item.Name.Trim();
        var category = item.Category.Trim();
        if (name.Length == 0 || category.Length == 0 || item.Price <= 0 ||
            item.PriceM is <= 0 || item.PriceG is <= 0 || item.PrintTarget is not ("kitchen" or "bar"))
        {
            MessageBox.Show("Informe nome, categoria, preços maiores que zero e destino kitchen ou bar.",
                "Dados inválidos", MessageBoxButton.OK, MessageBoxImage.Warning);
            await RefreshMenuAsync();
            return;
        }

        try
        {
            await _restaurant.SaveMenuItemAsync(item.ToRecord() with { Name = name, Category = category });
            FooterStatus.Text = $"Item “{name}” atualizado.";
            await RefreshMenuAsync();
        }
        catch (Exception exception)
        {
            await RefreshMenuAsync();
            ShowError("Não foi possível salvar as alterações no cardápio", exception);
        }
    }

    private async void NewEmployee_Click(object sender, RoutedEventArgs e)
    {
        await SaveEmployeeAsync(null);
    }

    private async void EditEmployee_Click(object sender, RoutedEventArgs e)
    {
        if (EmployeesGrid.SelectedItem is not RestaurantEmployeeRecord selected)
        {
            FooterStatus.Text = "Selecione um funcionário para editar.";
            return;
        }
        await SaveEmployeeAsync(selected);
    }

    private async Task SaveEmployeeAsync(RestaurantEmployeeRecord? existing)
    {
        var areas = (await _restaurant.GetAreasAsync())
            .Where(area => area.IsActive)
            .ToArray();
        var dialog = new WaiterAccountWindow(existing, areas) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        var employee = new RestaurantEmployeeRecord(
            existing?.Id ?? Guid.NewGuid(),
            dialog.EmployeeName,
            dialog.EmployeeRole,
            existing?.IsActive ?? true,
            dialog.WaiterNumber,
            existing?.FirebaseUid,
            string.Join(',', dialog.SelectedAreaIds),
            existing?.HasLocalPin ?? false);
        try
        {
            await _restaurant.SaveEmployeeAsync(employee);
            if (!string.IsNullOrEmpty(dialog.LocalPin))
            {
                await _restaurant.SetEmployeeLocalPinAsync(employee.Id, dialog.LocalPin);
                employee = employee with { HasLocalPin = true };
            }

            var refreshToken = await _localAccounts.GetRefreshTokenAsync();
            if (refreshToken is null ||
                (!employee.HasWebAccess && string.IsNullOrWhiteSpace(dialog.InitialPassword)))
            {
                await RefreshEmployeesAsync();
                FooterStatus.Text = $"Funcionário {employee.WaiterNumber} salvo com acesso local.";
                MessageBox.Show(
                    "Funcionário salvo neste computador com acesso local (PIN) para o app do garçom na rede Wi-Fi.\n\n" +
                    "Para liberar também o acesso web pela nuvem, conecte à internet, faça login online e edite o funcionário informando a senha inicial.",
                    "Acesso local salvo",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var firebase = new FirebaseStoreAccountClient(_accessControl);
            WaiterAccountProvisioningResult provisioning;
            try
            {
                provisioning = await firebase.CreateOrUpdateWaiterAccountAsync(
                    _storeAccount,
                    refreshToken,
                    employee,
                    dialog.SelectedAreaIds,
                    existing?.HasWebAccess == true ? null : dialog.InitialPassword);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                await RefreshEmployeesAsync();
                FooterStatus.Text = $"Funcionário {employee.WaiterNumber} salvo com acesso local (sem internet).";
                MessageBox.Show(
                    $"Sem conexão com a nuvem no momento, então o acesso web não foi configurado.\n\nO funcionário foi salvo localmente e o PIN local já funciona no app do garçom pela rede Wi-Fi.\n\nDetalhe: {exception.Message}",
                    "Acesso local salvo",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
            await _syncOutbox.UpdateRefreshTokenAsync(provisioning.StoreRefreshToken);

            employee = employee with { FirebaseUid = provisioning.FirebaseUid };
            await _restaurant.SaveEmployeeAsync(employee);
            await RefreshEmployeesAsync();
            FooterStatus.Text = $"Acesso web do garçom {employee.WaiterNumber} salvo.";
            MessageBox.Show(
                $"Funcionário cadastrado para acessar o webapp.\n\nCódigo da loja: {FirebaseStoreAccountClient.CreateWaiterStoreSlug(_storeAccount.CustomerNumber)}\nNúmero do garçom: {employee.WaiterNumber}\n\nA senha inicial não é exibida nem salva no computador; entregue-a ao funcionário por um canal seguro.",
                "Acesso do garçom criado",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            ShowError("Não foi possível cadastrar/sincronizar o acesso do garçom", exception);
            try
            {
                await RefreshEmployeesAsync();
            }
            catch (Exception refreshException)
            {
                ShowError("Não foi possível atualizar a lista de funcionários", refreshException);
            }
        }
        finally
        {
            dialog.ClearPassword();
        }
    }

    private async void ToggleEmployee_Click(object sender, RoutedEventArgs e)
    {
        if (EmployeesGrid.SelectedItem is not RestaurantEmployeeRecord employee)
            return;
        try
        {
            var updatedEmployee = employee with { IsActive = !employee.IsActive };
            if (employee.HasWebAccess)
            {
                var refreshToken = await _localAccounts.GetRefreshTokenAsync()
                                   ?? throw new InvalidOperationException(
                                       "Faça login online como lojista para alterar o acesso web do funcionário.");
                var areaIds = employee.AreaIds?
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(Guid.Parse)
                    .ToArray() ?? [];
                var provisioning = await new FirebaseStoreAccountClient(_accessControl)
                    .CreateOrUpdateWaiterAccountAsync(
                        _storeAccount,
                        refreshToken,
                        updatedEmployee,
                        areaIds,
                        initialPassword: null);
                await _syncOutbox.UpdateRefreshTokenAsync(provisioning.StoreRefreshToken);
            }
            await _restaurant.SaveEmployeeAsync(updatedEmployee);
            await RefreshEmployeesAsync();
            FooterStatus.Text = updatedEmployee.IsActive
                ? "Funcionário ativado e acesso web liberado."
                : "Funcionário desativado e acesso web revogado.";
        }
        catch (Exception exception)
        {
            ShowError("Não foi possível atualizar o funcionário", exception);
        }
    }

    private void EmployeesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ToggleEmployeeButton is not null)
            ToggleEmployeeButton.ToolTip = (EmployeesGrid.SelectedItem as RestaurantEmployeeRecord)?.IsActive == true
                ? "Desativar"
                : "Ativar";
    }

    private async void OpenCash_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new RestaurantEntryWindow("Abrir caixa", ["Valor do fundo inicial"]) { Owner = this };
        if (dialog.ShowDialog() != true ||
            !RestaurantEntryWindow.TryReadAmount(dialog.Values[0], out var amount) ||
            amount < 0)
        {
            if (dialog.DialogResult == true)
                MessageBox.Show("Informe um valor igual ou maior que zero.", "Valor inválido",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            await _restaurant.OpenCashSessionAsync(amount);
            await RefreshCashAsync();
            FooterStatus.Text = "Caixa aberto.";
        }
        catch (Exception exception)
        {
            ShowError("Não foi possível abrir o caixa", exception);
        }
    }

    private async void CloseCash_Click(object sender, RoutedEventArgs e)
    {
        var session = await _restaurant.GetOpenCashSessionAsync();
        if (session is null)
            return;
        var dialog = new RestaurantEntryWindow("Encerrar caixa", ["Dinheiro contado no caixa"]) { Owner = this };
        if (dialog.ShowDialog() != true ||
            !RestaurantEntryWindow.TryReadAmount(dialog.Values[0], out var amount) ||
            amount < 0)
        {
            if (dialog.DialogResult == true)
                MessageBox.Show("Informe um valor contado igual ou maior que zero.", "Valor inválido",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            await _restaurant.CloseCashSessionAsync(session.Id, amount);
            await RefreshCashAsync();
            MessageBox.Show($"Caixa encerrado.\nDinheiro esperado: {session.OpeningAmount + session.CashSales - session.CashExpenses:C2}\nDinheiro contado: {amount:C2}",
                "Fechamento de caixa", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            ShowError("Não foi possível encerrar o caixa", exception);
        }
    }

    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "Deseja sair da conta? Na próxima abertura será necessário entrar novamente. Os dados locais serão mantidos.",
                "Sair da conta", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        try
        {
            await _localAccounts.DisableAutoLoginAsync();
            Close();
        }
        catch (Exception exception)
        {
            ShowError("Não foi possível encerrar a sessão", exception);
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private sealed class MenuItemEditorRow(RestaurantMenuItemRecord item)
    {
        public Guid Id { get; } = item.Id;
        public string Name { get; set; } = item.Name;
        public string Category { get; set; } = item.Category;
        public string Description { get; set; } = item.Description;
        public decimal Price { get; set; } = item.Price;
        public bool IsActive { get; } = item.IsActive;
        public Guid? InventoryProductId { get; } = item.InventoryProductId;
        public decimal InventoryQuantityPerUnit { get; } = item.InventoryQuantityPerUnit;
        public string PrintTarget { get; set; } = item.PrintTarget;
        public decimal? PriceM { get; set; } = item.PriceM;
        public decimal? PriceG { get; set; } = item.PriceG;
        public string StatusLabel => IsActive ? "Ativo" : "Inativo";

        public RestaurantMenuItemRecord ToRecord() =>
            new(Id, Name, Category, Price, IsActive, InventoryProductId, InventoryQuantityPerUnit, PrintTarget: PrintTarget, PriceM: PriceM, PriceG: PriceG, Description: Description);
    }

    private sealed record MenuTemplateOption(string Name, IReadOnlyList<MenuTemplateItem> Items);
    private sealed record MenuTemplateItem(string Name, string Category, decimal Price);

    private static void ShowError(string title, Exception exception) =>
        MessageBox.Show($"{title}.\n\n{exception.Message}", "SYSMR Restaurante",
            MessageBoxButton.OK, MessageBoxImage.Error);
}
