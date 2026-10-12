using System.Collections.ObjectModel;
using System.IO;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SYSMR.Application.Abstractions;
using SYSMR.Application.Models;
using SYSMR.Application.Services;
using SYSMR.Domain.Entities;
using SYSMR.Infrastructure.Persistence;

namespace SYSMR.UI;

public partial class MainWindow : Window
{
    private readonly ProductCatalogService _catalog;
    private readonly SalesService _sales;
    private readonly IProductRepository _products;
    private readonly ICustomerRepository _customers;
    private readonly ISellerRepository _sellers;
    private readonly SqliteAccessControl _accessControl;
    private readonly IAuditLogRepository _auditLog;
    private readonly SqliteStoreAccountRepository _localAccounts;
    private readonly SqliteSyncOutboxRepository _syncOutbox;
    private readonly StoreTypeSwitchService _storeTypeSwitcher;
    private readonly StoreAccountIdentity? _storeAccount;
    private readonly string _storeName;
    private readonly ObservableCollection<CartRow> _cart = [];
    private readonly ObservableCollection<Product> _searchResults = [];
    private readonly ObservableCollection<string> _installedPrinters = [];
    private readonly ObservableCollection<ReturnItemRow> _returnItems = [];
    private string? _selectedPrinter;
    private bool _showOnlyLowStock;
    private bool _isAdministratorSession;
    private string _receiptStoreName = "SYSMR";
    private string _receiptPhone = string.Empty;
    private string _receiptAddress = string.Empty;
    private string _receiptFooter = string.Empty;

    public MainWindow(
        ProductCatalogService catalog,
        SalesService sales,
        IProductRepository products,
        ICustomerRepository customers,
        ISellerRepository sellers,
        SqliteAccessControl accessControl,
        IAuditLogRepository auditLog,
        SqliteStoreAccountRepository localAccounts,
        SqliteSyncOutboxRepository syncOutbox,
        StoreTypeSwitchService storeTypeSwitcher,
        StoreAccountIdentity? storeAccount,
        string storeName,
        bool startAsAdministrator)
    {
        InitializeComponent();
        _catalog = catalog;
        _sales = sales;
        _products = products;
        _customers = customers;
        _sellers = sellers;
        _accessControl = accessControl;
        _auditLog = auditLog;
        _localAccounts = localAccounts;
        _syncOutbox = syncOutbox;
        _storeTypeSwitcher = storeTypeSwitcher;
        _storeAccount = storeAccount;
        _storeName = storeName;
        _isAdministratorSession = startAsAdministrator;
        StoreTypeToggle.IsChecked = storeAccount?.StoreType == "restaurant";
        DataContext = this;
        OperatorLabel.Text = $"Loja: {storeName}  |  Vendedor: solicitado na venda";
        PrinterCombo.ItemsSource = _installedPrinters;
        StateChanged += (_, _) => UpdateWindowStateButton();
        UpdateWindowStateButton();
        UpdateAdministratorVisibility();
        Loaded += async (_, _) =>
        {
            try
            {
                _selectedPrinter = await _accessControl.GetSettingAsync("printer.name");
                _receiptStoreName = await _accessControl.GetSettingAsync("receipt.storeName") ?? _storeName;
                _receiptPhone = await _accessControl.GetSettingAsync("receipt.phone") ?? string.Empty;
                _receiptAddress = await _accessControl.GetSettingAsync("receipt.address") ?? string.Empty;
                _receiptFooter = await _accessControl.GetSettingAsync("receipt.footer") ?? string.Empty;
                await RefreshCustomersAsync();
                await RefreshSyncStatusAsync();
                var syncTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                syncTimer.Tick += async (_, _) => await RefreshSyncStatusAsync();
                syncTimer.Start();
                await TryLogActionAsync("Aplicativo aberto", $"Loja ativa: {_storeName}.");
                BarcodeBox.Focus();
            }
            catch (Exception exception)
            {
                ShowOperationError("Não foi possível carregar os clientes locais", exception);
            }
        };
    }

    public ObservableCollection<CartRow> CartItems => _cart;
    public ObservableCollection<Product> SearchResults => _searchResults;

    private async Task RefreshSyncStatusAsync()
    {
        try
        {
            var status = await _syncOutbox.GetStatusAsync("sales");
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
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximizeRestoreWindow_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateWindowStateButton()
    {
        if (MaximizeRestoreButton is null)
            return;

        var isMaximized = WindowState == WindowState.Maximized;
        MaximizeRestoreButton.Content = isMaximized ? "❐" : "□";
        MaximizeRestoreButton.ToolTip = isMaximized ? "Restaurar" : "Maximizar";
    }

    private async void BarcodeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        await AddBarcodeAsync();
    }

    private async void BarcodeAdd_Click(object sender, RoutedEventArgs e) => await AddBarcodeAsync();

    private async Task AddBarcodeAsync()
    {
        var barcode = BarcodeBox.Text.Trim();
        if (barcode.Length == 0)
            return;

        try
        {
            var scanned = await _catalog.FindScannedProductAsync(barcode);
            if (scanned is null)
            {
                StatusText.Text = $"Produto não encontrado para o código {barcode}.";
                BarcodeBox.SelectAll();
                return;
            }

            AddToCart(scanned);
            StatusText.Text = $"{scanned.Product.Name} adicionado.";
            BarcodeBox.Clear();
            BarcodeBox.Focus();
        }
        catch (Exception exception)
        {
            ShowOperationError("Não foi possível localizar o produto", exception);
        }
    }

    private void AddToCart(ScannedProduct scanned)
    {
        var row = _cart.FirstOrDefault(item => item.ProductId == scanned.Product.Id);
        if (row is null)
            _cart.Add(new CartRow(scanned.Product.Id, scanned.Product.Name, scanned.Quantity, scanned.Product.SalePrice));
        else
            row.Quantity += scanned.Quantity;
        RefreshCart();
    }

    private async void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded)
            return;
        await RefreshSearchAsync();
    }

    private async Task RefreshSearchAsync()
    {
        try
        {
            var results = await _catalog.SearchAsync(SearchBox.Text);
            _searchResults.Clear();
            foreach (var product in results)
                _searchResults.Add(product);
        }
        catch (Exception exception)
        {
            ShowOperationError("Não foi possível pesquisar produtos", exception);
        }
    }

    private void SearchGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => AddSelectedSearchResult();

    private void AddSelectedSearchResult()
    {
        if (SearchGrid.SelectedItem is Product product)
        {
            AddToCart(new ScannedProduct(product, 1m));
            StatusText.Text = $"{product.Name} adicionado.";
            BarcodeBox.Focus();
        }
    }

    private async void Pay_Click(object sender, RoutedEventArgs e) => await CompleteSaleAsync();

    private async Task CompleteSaleAsync()
    {
        if (_cart.Count == 0)
        {
            StatusText.Text = "Adicione ao menos um produto antes de finalizar.";
            BarcodeBox.Focus();
            return;
        }

        IReadOnlyList<SellerRecord> sellers;
        try
        {
            sellers = await _sellers.GetAllAsync(activeOnly: true);
        }
        catch (Exception exception)
        {
            ShowOperationError("Não foi possível carregar os vendedores cadastrados", exception);
            return;
        }
        if (sellers.Count == 0)
        {
            if (MessageBox.Show(
                    "Não há vendedores ativos cadastrados. Deseja abrir as Configurações para cadastrar um?",
                    "Vendedor necessário",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information) == MessageBoxResult.Yes)
            {
                ShowPage("Setup");
                await RefreshSetupAsync();
            }
            return;
        }
        var paymentWindow = new PaymentWindow(Total, sellers.Select(seller => seller.Name).ToArray()) { Owner = this };
        if (paymentWindow.ShowDialog() != true)
            return;

        try
        {
            var items = _cart.Select(row => new SaleItem(row.ProductId, row.Name, row.Quantity, row.UnitPrice)).ToArray();
            var customer = CustomerSelector.SelectedItem as Customer;
            var sellerName = paymentWindow.SellerName;
            var customerName = customer?.Name ?? "Consumidor final";
            var completedAt = DateTimeOffset.UtcNow;
            var saleId = await _sales.CompleteAsync(
                items,
                paymentWindow.SelectedPayment,
                CustomerBox.Text,
                customer?.Id,
                sellerName);
            _cart.Clear();
            CustomerSelector.SelectedItem = null;
            CustomerBox.Clear();
            RefreshCart();
            StatusText.Text = "Venda salva localmente. NFC-e não emitida.";
            if (ManagerPage.Visibility == Visibility.Visible)
                await RefreshManagerAsync();
            if (SalesPage.Visibility == Visibility.Visible)
                await RefreshSalesAsync();
            MessageBox.Show(
                "A venda foi salva no banco local e o estoque foi atualizado.\nA emissão da NFC-e ainda não está integrada.",
                "Venda registrada",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            if (MessageBox.Show(
                    "Venda registrada. Deseja imprimir o comprovante não fiscal agora?",
                    "Imprimir comprovante",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                PrintReceipt(
                    "COMPROVANTE DE VENDA — NÃO FISCAL",
                    saleId,
                    customerName,
                    sellerName,
                    paymentWindow.SelectedPayment.ToString(),
                    completedAt,
                    items,
                    items.Sum(item => item.Total));
            }
            BarcodeBox.Focus();
        }
        catch (Exception exception)
        {
            ShowOperationError("A venda não foi registrada", exception);
        }
    }

    private decimal Total => _cart.Sum(row => row.Total);

    private void RefreshCart()
    {
        TotalText.Text = Total.ToString("C2");
        CartCountText.Text = $"{_cart.Sum(row => row.Quantity):N3} item(ns)";
        CartGrid.Items.Refresh();
    }

    private async void Navigation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string target })
            return;

        if (target is "Customers" or "Audit" &&
            !await EnsureAdministratorAccessAsync())
            return;

        ShowPage(target);
        try
        {
            switch (target)
            {
                case "Manager":
                    await RefreshManagerAsync();
                    break;
                case "Sales":
                    await RefreshSalesAsync();
                    break;
                case "Returns":
                    await RefreshReturnsAsync();
                    break;
                case "Customers":
                    await RefreshCustomersAsync();
                    break;
                case "Setup":
                    await RefreshSetupAsync();
                    break;
                case "Pdv":
                    BarcodeBox.Focus();
                    break;
                case "Audit":
                    await RefreshAuditAsync();
                    break;
            }
        }
        catch (Exception exception)
        {
            ShowOperationError("Não foi possível abrir a área selecionada", exception);
        }
    }

    private void ShowPage(string target)
    {
        PdvPage.Visibility = target == "Pdv" ? Visibility.Visible : Visibility.Collapsed;
        ManagerPage.Visibility = target == "Manager" ? Visibility.Visible : Visibility.Collapsed;
        SalesPage.Visibility = target == "Sales" ? Visibility.Visible : Visibility.Collapsed;
        ReturnsPage.Visibility = target == "Returns" ? Visibility.Visible : Visibility.Collapsed;
        CustomersPage.Visibility = target == "Customers" ? Visibility.Visible : Visibility.Collapsed;
        SetupPage.Visibility = target == "Setup" ? Visibility.Visible : Visibility.Collapsed;
        HelpPage.Visibility = target == "Help" ? Visibility.Visible : Visibility.Collapsed;
        AuditPage.Visibility = target == "Audit" ? Visibility.Visible : Visibility.Collapsed;
        PageHeading.Text = target switch
        {
            "Pdv" => "Ponto de venda",
            "Manager" => "Estoque e painel do gestor",
            "Sales" => "Histórico de vendas",
            "Returns" => "Devoluções",
            "Customers" => "Cadastro de clientes",
            "Setup" => "Configurações da loja",
            "Audit" => "Log de ações",
            _ => "Ajuda e treinamento"
        };
    }

    private async Task<bool> EnsureAdministratorAccessAsync()
    {
        if (_isAdministratorSession)
            return true;
        var prompt = new AdminPinWindow(_accessControl, initialSetup: false) { Owner = this };
        if (prompt.ShowDialog() != true)
            return false;
        _isAdministratorSession = true;
        UpdateAdministratorVisibility();
        await TryLogActionAsync("Acesso Super Admin", "Sessão administrativa iniciada.");
        return true;
    }

    private void UpdateAdministratorVisibility()
    {
        var visibility = _isAdministratorSession ? Visibility.Visible : Visibility.Collapsed;
        TopCustomersButton.Visibility = visibility;
        TopAuditButton.Visibility = visibility;
        SidebarAuditButton.Visibility = visibility;
        TopSetupButton.Visibility = Visibility.Visible;
        SidebarSetupButton.Visibility = Visibility.Visible;
        AdministratorSettingsPanel.Visibility = visibility;
        SettingsAdminButton.Content = _isAdministratorSession ? "Sair do Super Admin" : "Entrar como Super Admin";
        AdminAccessButton.Content = _isAdministratorSession ? "Sair do Super Admin" : "Entrar Super Admin";
        if (!_isAdministratorSession &&
            (CustomersPage.Visibility == Visibility.Visible ||
             AuditPage.Visibility == Visibility.Visible))
            ShowPage("Pdv");
    }

    private async void AdminAccess_Click(object sender, RoutedEventArgs e)
    {
        if (_isAdministratorSession)
        {
            await TryLogActionAsync("Acesso Super Admin", "Sessão administrativa encerrada.", "Super Admin");
            _isAdministratorSession = false;
            UpdateAdministratorVisibility();
            StatusText.Text = "Sessão Super Admin encerrada.";
            return;
        }

        await EnsureAdministratorAccessAsync();
    }

    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "Deseja sair da conta? Na próxima abertura será necessário entrar novamente. Os dados locais serão mantidos.",
                "Sair da conta",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try
        {
            await _localAccounts.DisableAutoLoginAsync();
            await TryLogActionAsync("Sessão da loja encerrada", "Login automático desativado pelo usuário.", _storeName);
            Close();
        }
        catch (Exception exception)
        {
            ShowOperationError("Não foi possível encerrar a sessão da loja", exception);
        }
    }

    private async Task RefreshManagerAsync()
    {
        try
        {
            var total = await _sales.GetSalesTotalAsync();
            var count = await _sales.GetSalesCountAsync();
            var products = await _products.SearchAsync(string.Empty);
            var lowStockCount = (await _products.SearchLowStockAsync(2m)).Count;
            ManagerSalesTotal.Text = total.ToString("C2");
            ManagerSalesCount.Text = count.ToString();
            ManagerProductCount.Text = products.Count.ToString();
            InventoryStatusText.Text = _showOnlyLowStock
                ? $"Produtos com estoque de 2 ou menos: {lowStockCount}"
                : $"Todos os produtos • {lowStockCount} com estoque de 2 ou menos";
            InventoryGrid.ItemsSource = _showOnlyLowStock
                ? await _products.SearchLowStockAsync(2m)
                : products;
        }
        catch (Exception exception)
        {
            ShowOperationError("Não foi possível carregar o painel", exception);
        }
    }

    private async void LowStock_Click(object sender, RoutedEventArgs e)
    {
        _showOnlyLowStock = !_showOnlyLowStock;
        LowStockButton.Content = _showOnlyLowStock ? "Mostrar todos" : "⚠  Ver estoque baixo (≤ 2)";
        await RefreshManagerAsync();
    }

    private async void NewProduct_Click(object sender, RoutedEventArgs e)
    {
        var editor = new ProductEditorWindow(null) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is null)
            return;
        await SaveProductAsync(editor.Result);
    }

    private async void EditProduct_Click(object sender, RoutedEventArgs e)
    {
        if (InventoryGrid.SelectedItem is not Product selected)
        {
            StatusText.Text = "Selecione um produto para editar.";
            return;
        }
        var editor = new ProductEditorWindow(selected) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is null)
            return;
        await SaveProductAsync(editor.Result);
    }

    private async Task SaveProductAsync(Product product)
    {
        try
        {
            await _products.SaveAsync(product);
            await TryLogActionAsync("Produto salvo", $"Produto {product.Name}; unidade {product.UnitOfMeasure}; estoque {product.Stock}.");
            StatusText.Text = $"Produto '{product.Name}' salvo.";
            await RefreshManagerAsync();
        }
        catch (Exception exception)
        {
            ShowOperationError("Não foi possível salvar o produto. Verifique se o código de barras já está cadastrado", exception);
        }
    }

    private async Task RefreshSalesAsync()
    {
        SalesGrid.ItemsSource = await _sales.GetSalesAsync(SalesSearchBox.Text);
        SaleDetailsGrid.ItemsSource = null;
    }

    private async Task RefreshReturnsAsync()
    {
        ReturnsGrid.ItemsSource = await _sales.GetSalesAsync(string.Empty);
        ReturnDetailsGrid.ItemsSource = null;
        ReturnButton.IsEnabled = false;
    }

    private async Task RefreshCustomersAsync()
    {
        var customers = await _customers.SearchAsync(string.Empty);
        CustomersGrid.ItemsSource = customers;
        CustomerSelector.ItemsSource = customers;
        var needsReview = customers.Count(customer => customer.NeedsReview);
        CustomerExpirySummary.Text = $"{customers.Count} clientes • {needsReview} precisam de revisão. Vencimento não bloqueia vendas.";
    }

    private async Task RefreshAuditAsync()
    {
        AuditGrid.ItemsSource = await _auditLog.SearchAsync(AuditSearchBox.Text);
    }

    private async Task TryLogActionAsync(string action, string details, string? actor = null)
    {
        try
        {
            await _auditLog.RecordAsync(actor ?? (_isAdministratorSession ? "Super Admin" : _storeName), action, details);
        }
        catch (Microsoft.Data.Sqlite.SqliteException exception)
        {
            StatusText.Text = $"Ação concluída, mas o log falhou: {exception.Message}";
            MessageBox.Show(
                $"A ação foi concluída, mas não foi possível gravá-la no log de auditoria.\n\n{exception.Message}",
                "Falha no log de auditoria",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task RefreshSetupAsync()
    {
        DatabasePathText.Text = $"Banco local: {Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SYSMR", "sysmr.db")}";
        _receiptStoreName = await _accessControl.GetSettingAsync("receipt.storeName") ?? _storeName;
        _receiptPhone = await _accessControl.GetSettingAsync("receipt.phone") ?? string.Empty;
        _receiptAddress = await _accessControl.GetSettingAsync("receipt.address") ?? string.Empty;
        _receiptFooter = await _accessControl.GetSettingAsync("receipt.footer") ?? string.Empty;
        ReceiptStoreNameBox.Text = _receiptStoreName;
        ReceiptPhoneBox.Text = _receiptPhone;
        ReceiptAddressBox.Text = _receiptAddress;
        ReceiptFooterBox.Text = _receiptFooter;
        SellersGrid.ItemsSource = await _sellers.GetAllAsync();
        UpdateSellerActions();
        FirebaseProjectBox.Text = await _accessControl.GetSettingAsync("firebase.projectId") ?? string.Empty;
        FirebaseApiKeyBox.Text = await _accessControl.GetSettingAsync("firebase.webApiKey") ?? string.Empty;
        FirebaseUrlBox.Text = await _accessControl.GetSettingAsync("firebase.databaseUrl") ?? string.Empty;
        FirebaseProvisioningUrlBox.Text = await _accessControl.GetSettingAsync("firebase.provisioningUrl") ?? string.Empty;
        var selected = await _accessControl.GetSettingAsync("printer.name");
        _selectedPrinter = selected;
        PrinterAddressBox.Text = await _accessControl.GetSettingAsync("printer.networkAddress") ?? string.Empty;
        RefreshInstalledPrinters();
        if (selected is not null)
            PrinterCombo.SelectedItem = _installedPrinters.FirstOrDefault(name => name.Equals(selected, StringComparison.OrdinalIgnoreCase));
    }

    private async void SaveStoreSettings_Click(object sender, RoutedEventArgs e)
    {
        var storeName = ReceiptStoreNameBox.Text.Trim();
        if (storeName.Length == 0)
            storeName = _storeName;

        try
        {
            await _accessControl.SetSettingAsync("receipt.storeName", storeName);
            await _accessControl.SetSettingAsync("receipt.phone", ReceiptPhoneBox.Text.Trim());
            await _accessControl.SetSettingAsync("receipt.address", ReceiptAddressBox.Text.Trim());
            await _accessControl.SetSettingAsync("receipt.footer", ReceiptFooterBox.Text.Trim());
            _receiptStoreName = storeName;
            _receiptPhone = ReceiptPhoneBox.Text.Trim();
            _receiptAddress = ReceiptAddressBox.Text.Trim();
            _receiptFooter = ReceiptFooterBox.Text.Trim();
            await TryLogActionAsync("Configuração da loja atualizada", "Dados comerciais do comprovante atualizados.");
            StatusText.Text = "Dados da loja salvos.";
        }
        catch (Exception exception)
        {
            ShowOperationError("Não foi possível salvar os dados da loja", exception);
        }
    }

    private async void NewSeller_Click(object sender, RoutedEventArgs e)
    {
        var editor = new SellerEditorWindow(null) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is null)
            return;
        await SaveSellerAsync(editor.Result, "Vendedor cadastrado");
    }

    private async void EditSeller_Click(object sender, RoutedEventArgs e)
    {
        if (SellersGrid.SelectedItem is not SellerRecord seller)
        {
            StatusText.Text = "Selecione um vendedor para editar.";
            return;
        }

        var editor = new SellerEditorWindow(seller) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is null)
            return;
        await SaveSellerAsync(editor.Result, "Vendedor atualizado");
    }

    private async void ToggleSeller_Click(object sender, RoutedEventArgs e)
    {
        if (SellersGrid.SelectedItem is not SellerRecord seller)
            return;
        await SaveSellerAsync(seller with { IsActive = !seller.IsActive },
            seller.IsActive ? "Vendedor desativado" : "Vendedor reativado");
    }

    private async Task SaveSellerAsync(SellerRecord seller, string action)
    {
        try
        {
            await _sellers.SaveAsync(seller);
            await TryLogActionAsync(action, $"Vendedor: {seller.Name}.");
            await RefreshSetupAsync();
            StatusText.Text = $"{seller.Name} salvo.";
        }
        catch (Exception exception)
        {
            ShowOperationError("Não foi possível salvar o vendedor", exception);
        }
    }

    private void SellersGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSellerActions();

    private void UpdateSellerActions()
    {
        if (ToggleSellerButton is null)
            return;
        var selected = SellersGrid.SelectedItem as SellerRecord;
        ToggleSellerButton.IsEnabled = selected is not null;
        ToggleSellerButton.Content = selected?.IsActive == true ? "Desativar" : "Ativar";
    }

    private void RefreshInstalledPrinters()
    {
        try
        {
            _installedPrinters.Clear();
            using var server = new LocalPrintServer();
            foreach (var queue in server.GetPrintQueues())
            {
                var name = queue.Name;
                if (!_installedPrinters.Contains(name, StringComparer.OrdinalIgnoreCase))
                    _installedPrinters.Add(name);
            }
            if (_installedPrinters.Count == 0)
                StatusText.Text = "O Windows não informou impressoras instaladas.";
        }
        catch (Exception exception)
        {
            ShowOperationError("Não foi possível listar impressoras do Windows", exception);
        }
    }

    private async void SalesSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded && SalesPage.Visibility == Visibility.Visible)
        {
            try
            {
                await RefreshSalesAsync();
            }
            catch (Exception exception)
            {
                ShowOperationError("Não foi possível pesquisar vendas", exception);
            }
        }
    }

    private async void AuditSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || AuditPage.Visibility != Visibility.Visible)
            return;
        try
        {
            await RefreshAuditAsync();
        }
        catch (Exception exception)
        {
            ShowOperationError("Não foi possível consultar o log", exception);
        }
    }

    private async void SalesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SalesGrid.SelectedItem is SaleHistoryEntry selected)
        {
            try
            {
                var sale = await _sales.GetSaleAsync(selected.Id);
                SaleDetailsGrid.ItemsSource = sale?.Items;
            }
            catch (Exception exception)
            {
                ShowOperationError("Não foi possível abrir os itens da venda", exception);
            }
        }
    }

    private async void ReturnsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReturnsGrid.SelectedItem is SaleHistoryEntry selected)
        {
            try
            {
                var sale = await _sales.GetSaleAsync(selected.Id);
                _returnItems.Clear();
                if (sale is not null)
                {
                    foreach (var item in sale.Items)
                    {
                        var alreadyReturned = sale.ReturnedQuantities.GetValueOrDefault(item.ProductId);
                        _returnItems.Add(new ReturnItemRow(
                            item.ProductId,
                            item.ProductName,
                            item.Quantity,
                            alreadyReturned,
                            item.UnitPrice));
                    }
                }
                ReturnDetailsGrid.ItemsSource = _returnItems;
                ReturnButton.IsEnabled = _returnItems.Any(item => item.AvailableQuantity > 0);
            }
            catch (Exception exception)
            {
                ShowOperationError("Não foi possível abrir os itens para devolução", exception);
            }
        }
        else
        {
            ReturnButton.IsEnabled = false;
        }
    }

    private async void PrintSaleRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SaleHistoryEntry selected })
            return;

        try
        {
            var sale = await _sales.GetSaleAsync(selected.Id);
            if (sale is null)
            {
                StatusText.Text = "A venda selecionada não foi encontrada.";
                return;
            }

            PrintReceipt(
                "COMPROVANTE DE VENDA — NÃO FISCAL",
                sale.Summary.Id,
                sale.Summary.Customer,
                sale.Summary.SellerName,
                sale.Summary.PaymentLabel,
                sale.Summary.CreatedAt,
                sale.Items,
                sale.Summary.Total);
        }
        catch (Exception exception)
        {
            ShowOperationError("Não foi possível carregar o comprovante da venda", exception);
        }
    }

    private async void PrintReturnRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SaleHistoryEntry selected })
            return;

        try
        {
            var sale = await _sales.GetSaleAsync(selected.Id);
            if (sale is null)
            {
                StatusText.Text = "A venda selecionada não foi encontrada.";
                return;
            }

            var returnedItems = sale.Items
                .Where(item => sale.ReturnedQuantities.GetValueOrDefault(item.ProductId) > 0)
                .Select(item => new SaleItem(
                    item.ProductId,
                    item.ProductName,
                    sale.ReturnedQuantities[item.ProductId],
                    item.UnitPrice))
                .ToArray();
            if (returnedItems.Length == 0)
            {
                MessageBox.Show(
                    "Ainda não há itens devolvidos nesta venda.",
                    "Comprovante de devolução",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            PrintReceipt(
                "RESUMO DE DEVOLUÇÕES — NÃO FISCAL",
                sale.Summary.Id,
                sale.Summary.Customer,
                "Devolução",
                sale.Summary.PaymentLabel,
                DateTimeOffset.Now,
                returnedItems,
                returnedItems.Sum(item => item.Total));
        }
        catch (Exception exception)
        {
            ShowOperationError("Não foi possível carregar o comprovante das devoluções", exception);
        }
    }

    private async void ReturnSale_Click(object sender, RoutedEventArgs e)
    {
        if (ReturnsGrid.SelectedItem is not SaleHistoryEntry selected)
            return;
        ReturnDetailsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        ReturnDetailsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var selectedItems = _returnItems
            .Where(item => item.QuantityToReturn > 0)
            .Select(item => new SaleItem(item.ProductId, item.ProductName, item.QuantityToReturn, item.UnitPrice))
            .ToArray();
        var invalidQuantity = _returnItems.FirstOrDefault(item =>
            item.QuantityToReturn < 0 || item.QuantityToReturn > item.AvailableQuantity);
        if (invalidQuantity is not null)
        {
            MessageBox.Show(
                $"A quantidade a devolver de '{invalidQuantity.ProductName}' deve estar entre zero e {invalidQuantity.AvailableQuantity:N3}.",
                "Quantidade inválida",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        if (selectedItems.Length == 0)
        {
            MessageBox.Show("Informe uma quantidade maior que zero em pelo menos um item.", "Devolução",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var amount = selectedItems.Sum(item => item.Total);
        if (MessageBox.Show(
                $"Registrar a devolução dos itens selecionados no total de {amount:C2}?\nO estoque será reposto nas quantidades informadas e o caixa receberá o estorno pelo meio de pagamento original.",
                "Confirmar devolução",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        try
        {
            await _sales.ReturnSaleAsync(selected.Id, selectedItems, $"Loja ({_storeName})");
            StatusText.Text = $"Devolução de {amount:C2} registrada; estoque e caixa foram atualizados.";
            if (MessageBox.Show(
                    "Devolução registrada. Deseja imprimir o comprovante agora?",
                    "Imprimir comprovante",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                PrintReceipt(
                    "COMPROVANTE DE DEVOLUÇÃO — NÃO FISCAL",
                    selected.Id,
                    selected.Customer,
                    "Devolução",
                    selected.PaymentLabel,
                    DateTimeOffset.Now,
                    selectedItems,
                    amount);
            }
            await RefreshReturnsAsync();
            await RefreshManagerAsync();
        }
        catch (Exception exception)
        {
            ShowOperationError("Não foi possível registrar a devolução", exception);
        }
    }

    private void PrintReceipt(
        string title,
        Guid transactionId,
        string customer,
        string seller,
        string payment,
        DateTimeOffset createdAt,
        IReadOnlyCollection<SaleItem> items,
        decimal total)
    {
        var printDialog = new PrintDialog();
        if (!string.IsNullOrWhiteSpace(_selectedPrinter))
        {
            var configuredQueue = FindInstalledQueue(_selectedPrinter);
            if (configuredQueue is not null)
                printDialog.PrintQueue = configuredQueue;
        }

        if (printDialog.ShowDialog() != true)
        {
            StatusText.Text = "Impressão cancelada; a operação permanece registrada.";
            return;
        }

        try
        {
            var document = CreateReceiptDocument(
                title, transactionId, customer, seller, payment, createdAt, items, total,
                _receiptStoreName, _receiptPhone, _receiptAddress, _receiptFooter);
            var pageWidth = 80 * 96 / 25.4;
            var pageHeight = Math.Max(420, (document.Blocks.Count * 42 + items.Count * 22) * 96 / 72);
            document.PageWidth = pageWidth;
            document.PageHeight = pageHeight;
            document.ColumnWidth = pageWidth - document.PagePadding.Left - document.PagePadding.Right;
            printDialog.PrintDocument(((IDocumentPaginatorSource)document).DocumentPaginator, title);
            StatusText.Text = $"Comprovante enviado para {printDialog.PrintQueue.FullName}.";
        }
        catch (Exception exception)
        {
            ShowOperationError("A operação foi registrada, mas não foi possível imprimir o comprovante", exception);
        }
    }

    private static FlowDocument CreateReceiptDocument(
        string title,
        Guid transactionId,
        string customer,
        string seller,
        string payment,
        DateTimeOffset createdAt,
        IReadOnlyCollection<SaleItem> items,
        decimal total,
        string storeName,
        string phone,
        string address,
        string footer)
    {
        var document = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 9,
            PagePadding = new Thickness(14, 12, 14, 16),
            ColumnWidth = 80 * 96 / 25.4 - 28
        };
        document.Blocks.Add(new Paragraph(new Run(storeName))
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 2)
        });
        if (!string.IsNullOrWhiteSpace(phone))
            AddReceiptMetadata(document, "Telefone", phone);
        if (!string.IsNullOrWhiteSpace(address))
            AddReceiptMetadata(document, "Endereço", address);
        document.Blocks.Add(new Paragraph(new Run("COMPROVANTE DE ATENDIMENTO"))
        {
            FontSize = 9,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8)
        });
        document.Blocks.Add(new Paragraph(new Run(title.Replace(" — NÃO FISCAL", string.Empty, StringComparison.OrdinalIgnoreCase)))
        {
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8)
        });
        AddReceiptMetadata(document, "Data", createdAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm"));
        if (transactionId != Guid.Empty)
            AddReceiptMetadata(document, "Código", transactionId.ToString("N")[..12].ToUpperInvariant());
        AddReceiptMetadata(document, "Cliente", customer);
        AddReceiptMetadata(document, "Atendente", seller);
        AddReceiptMetadata(document, "Pagamento", payment);

        const double amountColumnWidth = 88;
        var itemTable = new Table { CellSpacing = 0, Margin = new Thickness(0, 8, 0, 0) };
        itemTable.Columns.Add(new TableColumn
        {
            Width = new GridLength(document.ColumnWidth - amountColumnWidth)
        });
        itemTable.Columns.Add(new TableColumn { Width = new GridLength(amountColumnWidth) });
        var itemGroup = new TableRowGroup();
        itemTable.RowGroups.Add(itemGroup);
        var heading = new TableRow();
        heading.Cells.Add(new TableCell(new Paragraph(new Run("ITEM")) { FontWeight = FontWeights.Bold }));
        heading.Cells.Add(new TableCell(new Paragraph(new Run("VALOR")) { FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Right }));
        itemGroup.Rows.Add(heading);

        foreach (var item in items)
        {
            var row = new TableRow();
            var description = new Paragraph { Margin = new Thickness(0, 4, 2, 4) };
            description.Inlines.Add(new Run(item.ProductName + "\n") { FontWeight = FontWeights.SemiBold });
            description.Inlines.Add(new Run($"{item.Quantity:N3} × {item.UnitPrice:C2}"));
            row.Cells.Add(new TableCell(description));
            row.Cells.Add(new TableCell(new Paragraph(new Run(item.Total.ToString("C2")))
            {
                TextAlignment = TextAlignment.Right,
                Margin = new Thickness(0, 4, 0, 4)
            }));
            itemGroup.Rows.Add(row);
        }

        document.Blocks.Add(itemTable);
        document.Blocks.Add(new Paragraph(new Run($"TOTAL  {total:C2}"))
        {
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Right,
            BorderBrush = new SolidColorBrush(Color.FromRgb(220, 228, 239)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 8, 0, 0),
            Margin = new Thickness(0, 7, 0, 0)
        });
        document.Blocks.Add(new Paragraph(new Run(string.IsNullOrWhiteSpace(footer) ? "Obrigado pela preferência!" : footer))
        {
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0)
        });
        document.Blocks.Add(new Paragraph(new Run("Comprovante não fiscal. Não substitui documento fiscal."))
        {
            FontSize = 7,
            Foreground = Brushes.Gray,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 5, 0, 0)
        });
        return document;
    }

    private static void AddReceiptMetadata(FlowDocument document, string label, string value)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
        paragraph.Inlines.Add(new Run($"{label}: ") { FontWeight = FontWeights.SemiBold });
        paragraph.Inlines.Add(new Run(value));
        document.Blocks.Add(paragraph);
    }

    private PrintQueue? FindInstalledQueue(string printerName)
    {
        try
        {
            using var server = new LocalPrintServer();
            return server.GetPrintQueues()
                .FirstOrDefault(queue => queue.Name.Equals(printerName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Não foi possível selecionar a impressora configurada; escolha-a no diálogo. {exception.Message}";
            return null;
        }
    }
    private async void NewCustomer_Click(object sender, RoutedEventArgs e)
    {
        var editor = new CustomerEditorWindow(null) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is null)
            return;
        await SaveCustomerAsync(editor.Result, isEdit: false);
    }

    private async void EditCustomer_Click(object sender, RoutedEventArgs e)
    {
        if (CustomersGrid.SelectedItem is not Customer selected)
        {
            StatusText.Text = "Selecione um cliente para editar.";
            return;
        }
        var editor = new CustomerEditorWindow(selected) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is null)
            return;
        await SaveCustomerAsync(editor.Result, isEdit: true);
    }

    private async Task SaveCustomerAsync(Customer customer, bool isEdit)
    {
        try
        {
            await _customers.SaveAsync(customer);
            await TryLogActionAsync(isEdit ? "Cliente editado" : "Cliente cadastrado", $"Cliente {customer.Name}; documento {customer.Document}.");
            await RefreshCustomersAsync();
            StatusText.Text = $"Cadastro de {customer.Name} salvo.";
        }
        catch (Exception exception)
        {
            ShowOperationError("Não foi possível salvar o cliente. Verifique se o CPF/CNPJ já está cadastrado", exception);
        }
    }

    private async void RenewCustomer_Click(object sender, RoutedEventArgs e)
    {
        if (CustomersGrid.SelectedItem is not Customer selected)
        {
            StatusText.Text = "Selecione um cliente para renovar o prazo.";
            return;
        }
        try
        {
            await _customers.RenewAsync(selected.Id, DateTimeOffset.UtcNow);
            await TryLogActionAsync("Cadastro de cliente renovado", $"Cliente {selected.Name}; revisão por 30 dias.");
            await RefreshCustomersAsync();
            StatusText.Text = $"Cadastro de {selected.Name} revisado por mais 30 dias.";
        }
        catch (Exception exception)
        {
            ShowOperationError("Não foi possível renovar o cadastro", exception);
        }
    }

    private async void SavePrinter_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (PrinterCombo.SelectedItem is not string printer)
            {
                StatusText.Text = "Selecione uma impressora instalada.";
                return;
            }
            await _accessControl.SetSettingAsync("printer.name", printer);
            _selectedPrinter = printer;
            await _accessControl.SetSettingAsync("printer.networkAddress", PrinterAddressBox.Text.Trim());
            await TryLogActionAsync("Impressora configurada", $"Fila do Windows: {printer}.");
            StatusText.Text = $"Impressora '{printer}' salva.";
        }
        catch (Exception exception)
        {
            ShowOperationError("Não foi possível salvar a impressora", exception);
        }
    }

    private async void SaveFirebase_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureAdministratorAccessAsync())
            return;
        try
        {
            var databaseUrl = FirebaseUrlBox.Text.Trim();
            var provisioningUrl = FirebaseProvisioningUrlBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(FirebaseProjectBox.Text) ||
                string.IsNullOrWhiteSpace(FirebaseApiKeyBox.Text) ||
                !Uri.TryCreate(databaseUrl, UriKind.Absolute, out var parsedUrl) ||
                parsedUrl.Scheme != Uri.UriSchemeHttps)
            {
                StatusText.Text = "Informe Project ID, Web API Key e uma URL HTTPS válida do Realtime Database.";
                return;
            }
            if (!Uri.TryCreate(provisioningUrl, UriKind.Absolute, out var parsedProvisioningUrl) ||
                parsedProvisioningUrl.Scheme != Uri.UriSchemeHttps)
            {
                StatusText.Text = "Informe uma URL HTTPS válida para a Cloud Function.";
                return;
            }
            await _accessControl.SetSettingAsync("firebase.projectId", FirebaseProjectBox.Text.Trim());
            await _accessControl.SetSettingAsync("firebase.webApiKey", FirebaseApiKeyBox.Text.Trim());
            await _accessControl.SetSettingAsync("firebase.databaseUrl", databaseUrl);
            await _accessControl.SetSettingAsync("firebase.provisioningUrl", provisioningUrl);
            await TryLogActionAsync("Firebase configurado", $"Project ID: {FirebaseProjectBox.Text.Trim()}; URL: {databaseUrl}.");
            StatusText.Text = "Parâmetros do Firebase salvos localmente. Autenticação e provisionamento de lojas usam Firebase.";
        }
        catch (Exception exception)
        {
            ShowOperationError("Não foi possível salvar os parâmetros", exception);
        }
    }

    private async void StoreTypeToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureAdministratorAccessAsync())
        {
            StoreTypeToggle.IsChecked = _storeAccount?.StoreType == "restaurant";
            return;
        }
        if (_storeAccount is null)
        {
            StoreTypeToggle.IsChecked = false;
            MessageBox.Show(
                "Entre primeiro com uma conta de loja. A sessão global do Super Admin não está vinculada a uma loja específica.",
                "Conta de loja necessária",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var newStoreType = StoreTypeToggle.IsChecked == true ? "restaurant" : "sales";
        var newFormat = newStoreType == "restaurant" ? "Restaurante" : "Lojista / PDV";
        if (MessageBox.Show(
                $"Alterar o formato desta loja para {newFormat}?\n\nO SYSMR será fechado para aplicar a mudança. Os dados da outra vertente serão mantidos.",
                "Confirmar alteração de formato",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            StoreTypeToggle.IsChecked = _storeAccount.StoreType == "restaurant";
            return;
        }

        try
        {
            await _storeTypeSwitcher.ChangeAsync(_storeAccount, newStoreType);
            await TryLogActionAsync(
                "Formato da loja alterado",
                $"Vertente alterada de {_storeAccount.StoreType} para {newStoreType}.",
                "Super Admin");
            MessageBox.Show(
                $"Formato alterado para {newFormat}. O SYSMR será fechado; abra o programa novamente para continuar.",
                "Formato atualizado",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Close();
        }
        catch (Exception exception)
        {
            StoreTypeToggle.IsChecked = _storeAccount.StoreType == "restaurant";
            ShowOperationError("Não foi possível alterar o formato da loja", exception);
        }
    }

    private void RefreshPrinters_Click(object sender, RoutedEventArgs e) => RefreshInstalledPrinters();

    private void PrinterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PrinterCombo.SelectedItem is string selected)
            StatusText.Text = $"Impressora selecionada: {selected}";
    }

    private void CustomerSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CustomerSelector.SelectedItem is not Customer customer)
            return;
        CustomerBox.Text = customer.Document;
        StatusText.Text = customer.NeedsReview
            ? $"Cadastro de {customer.Name} venceu e precisa de revisão. A venda continua permitida."
            : $"Cliente {customer.Name} selecionado.";
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2)
        {
            e.Handled = true;
            ShowPage("Pdv");
            SearchBox.Focus();
            SearchBox.SelectAll();
        }
        else if (e.Key == Key.F3)
        {
            e.Handled = true;
            ShowPage("Manager");
            var editor = new ProductEditorWindow(null) { Owner = this, AutoStartVoice = true };
            if (editor.ShowDialog() == true && editor.Result is not null)
                await SaveProductAsync(editor.Result);
        }
        else if (e.Key == Key.F4)
        {
            e.Handled = true;
            ShowPage("Pdv");
            CustomerBox.Focus();
        }
        else if (e.Key == Key.F5)
        {
            e.Handled = true;
            if (PdvPage.Visibility == Visibility.Visible)
                _ = CompleteSaleAsync();
        }
        else if (e.Key == Key.Escape && PdvPage.Visibility == Visibility.Visible)
        {
            e.Handled = true;
            if (CartGrid.SelectedItem is CartRow selected)
            {
                if (!await EnsureAdministratorAccessAsync())
                    return;
                _cart.Remove(selected);
                RefreshCart();
                await TryLogActionAsync("Item removido da venda", $"Produto {selected.Name}; quantidade {selected.Quantity}.");
                StatusText.Text = "Item removido após autorização do Super Admin.";
            }
            else if (_cart.Count > 0 &&
                     MessageBox.Show("Cancelar a venda atual?", "Cancelar venda", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                if (!await EnsureAdministratorAccessAsync())
                    return;
                if (_cart.Count > 0)
                {
                    var canceledTotal = Total;
                    var canceledCount = _cart.Count;
                    _cart.Clear();
                    RefreshCart();
                    await TryLogActionAsync("Venda cancelada", $"{canceledCount} produto(s); total não finalizado {canceledTotal:C2}.");
                    StatusText.Text = "Venda cancelada após autorização do Super Admin.";
                }
            }
            else
            {
                BarcodeBox.Focus();
            }
        }
    }

    private void CustomerBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            BarcodeBox.Focus();
        }

    }

    private void BarcodeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (BarcodeBox.Text.Length > 0 && BarcodeBox.Text.Any(char.IsWhiteSpace))
            BarcodeBox.Text = BarcodeBox.Text.Trim();
    }

    private void ShowOperationError(string title, Exception exception)
    {
        StatusText.Text = title + ".";
        MessageBox.Show($"{title}.\n\n{exception.Message}", "SYSMR", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    public sealed class CartRow(Guid productId, string name, decimal quantity, decimal unitPrice)
    {
        public Guid ProductId { get; } = productId;
        public string Name { get; } = name;
        public decimal Quantity { get; set; } = quantity;
        public decimal UnitPrice { get; } = unitPrice;
        public decimal Total => decimal.Round(Quantity * UnitPrice, 2, MidpointRounding.AwayFromZero);
    }
}
