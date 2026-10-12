using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SYSMR.Application.Abstractions;
using SYSMR.Application.Models;
using SYSMR.Domain.Entities;
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
                await RefreshAreasAsync();
                await RefreshCashAsync();
                await RefreshSyncStatusAsync();
                var syncTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                syncTimer.Tick += async (_, _) => await RefreshSyncStatusAsync();
                syncTimer.Start();
            }
            catch (Exception exception)
            {
                ShowError("Não foi possível iniciar o módulo de restaurante", exception);
            }
        };
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
        AreaCombo.ItemsSource = areas;
        SettingsAreasGrid.ItemsSource = areas;
        var selected = areas.FirstOrDefault(area => area.Id == selectedAreaId) ?? areas.FirstOrDefault();
        AreaCombo.SelectedItem = selected;
        SettingsAreasGrid.SelectedItem = selected;
        _loadingAreas = false;
        if (selected is not null)
            await RefreshTablesAsync();
    }

    private async Task RefreshTablesAsync()
    {
        if (AreaCombo.SelectedItem is not RestaurantAreaRecord area)
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

    private async Task RefreshCashAsync()
    {
        var session = await _restaurant.GetOpenCashSessionAsync();
        var isOpen = session is not null;
        OpenCashButton.IsEnabled = !isOpen;
        CloseCashButton.IsEnabled = isOpen;
        CashStatusText.Text = isOpen ? "Caixa aberto" : "Caixa fechado";
        CashOpeningText.Text = isOpen
            ? $"Abertura: {session!.OpenedAt.ToLocalTime():dd/MM/yyyy HH:mm} • Fundo inicial: {session.OpeningAmount:C2}"
            : "Abra o caixa para registrar pagamentos de comandas.";
        CashSalesText.Text = isOpen
            ? $"Recebimentos em dinheiro: {session!.CashSales:C2} • outras formas: {session.OtherSales:C2}"
            : string.Empty;
        CashExpectedText.Text = isOpen
            ? $"Dinheiro esperado em caixa: {session!.OpeningAmount + session.CashSales:C2}"
            : string.Empty;
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
            var orderWindow = new RestaurantOrderWindow(_restaurant, table, employees) { Owner = this };
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
        ShowPage("Employees");
        await RefreshEmployeesAsync();
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

    private async void NewMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await SaveMenuItemAsync(null);
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
                inventoryQuantity));
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
            ToggleMenuButton.Content = (MenuGrid.SelectedItem as MenuItemEditorRow)?.IsActive == true
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
        if (name.Length == 0 || category.Length == 0 || item.Price <= 0)
        {
            MessageBox.Show("Nome e categoria são obrigatórios, e o preço deve ser maior que zero.",
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
        var dialog = new RestaurantEntryWindow(
            existing is null ? "Novo funcionário" : "Editar funcionário",
            ["Nome", "Função"]) { Owner = this };
        if (existing is not null)
            dialog.Loaded += (_, _) =>
            {
                dialog.ValueOne.Text = existing.Name;
                dialog.ValueTwo.Text = existing.Role;
            };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            await _restaurant.SaveEmployeeAsync(new RestaurantEmployeeRecord(
                existing?.Id ?? Guid.NewGuid(), dialog.Values[0], dialog.Values[1], existing?.IsActive ?? true));
            await RefreshEmployeesAsync();
            FooterStatus.Text = "Funcionário salvo.";
        }
        catch (Exception exception)
        {
            ShowError("Não foi possível salvar o funcionário", exception);
        }
    }

    private async void ToggleEmployee_Click(object sender, RoutedEventArgs e)
    {
        if (EmployeesGrid.SelectedItem is not RestaurantEmployeeRecord employee)
            return;
        try
        {
            await _restaurant.SaveEmployeeAsync(employee with { IsActive = !employee.IsActive });
            await RefreshEmployeesAsync();
        }
        catch (Exception exception)
        {
            ShowError("Não foi possível atualizar o funcionário", exception);
        }
    }

    private void EmployeesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ToggleEmployeeButton is not null)
            ToggleEmployeeButton.Content = (EmployeesGrid.SelectedItem as RestaurantEmployeeRecord)?.IsActive == true
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
            MessageBox.Show($"Caixa encerrado.\nDinheiro esperado: {session.OpeningAmount + session.CashSales:C2}\nDinheiro contado: {amount:C2}",
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
        public decimal Price { get; set; } = item.Price;
        public bool IsActive { get; } = item.IsActive;
        public Guid? InventoryProductId { get; } = item.InventoryProductId;
        public decimal InventoryQuantityPerUnit { get; } = item.InventoryQuantityPerUnit;
        public string StatusLabel => IsActive ? "Ativo" : "Inativo";

        public RestaurantMenuItemRecord ToRecord() =>
            new(Id, Name, Category, Price, IsActive, InventoryProductId, InventoryQuantityPerUnit);
    }

    private sealed record MenuTemplateOption(string Name, IReadOnlyList<MenuTemplateItem> Items);
    private sealed record MenuTemplateItem(string Name, string Category, decimal Price);

    private static void ShowError(string title, Exception exception) =>
        MessageBox.Show($"{title}.\n\n{exception.Message}", "SYSMR Restaurante",
            MessageBoxButton.OK, MessageBoxImage.Error);
}
