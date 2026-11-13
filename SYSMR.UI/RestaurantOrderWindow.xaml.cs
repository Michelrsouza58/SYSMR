using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Globalization;
using System.Printing;
using System.Windows.Threading;
using SYSMR.Application.Models;
using SYSMR.Domain.Entities;
using SYSMR.Infrastructure;
using SYSMR.Infrastructure.Persistence;

namespace SYSMR.UI;

public partial class RestaurantOrderWindow : Window
{
    private readonly SqliteRestaurantRepository _restaurant;
    private readonly RestaurantTableRecord _table;
    private IReadOnlyList<RestaurantEmployeeRecord> _employees = [];
    private RestaurantOrderRecord? _order;
    private DispatcherTimer? _orderRefreshTimer;
    private bool _refreshingOrder;
    private readonly KitchenPrintService? _kitchen;
    private IReadOnlyList<RestaurantItemPaymentSelection> _paymentSelections = [];

    public RestaurantOrderWindow(
        SqliteRestaurantRepository restaurant,
        RestaurantTableRecord table,
        IReadOnlyList<RestaurantEmployeeRecord> employees,
        bool serviceChargeFeatureEnabled = true,
        KitchenPrintService? kitchen = null)
    {
        InitializeComponent();
        _restaurant = restaurant;
        _table = table;
        _employees = employees;
        _kitchen = kitchen;
        RestaurantOrderNotifier.OrderChanged += OnLocalOrderChanged;
        if (!serviceChargeFeatureEnabled)
        {
            WaiterServiceChargeCheckBox.Visibility = Visibility.Collapsed;
            ServiceChargeText.Visibility = Visibility.Collapsed;
        }
        TitleText.Text = $"Comanda • Mesa {table.Number}";
        EmployeeCombo.ItemsSource = employees;
        Loaded += async (_, _) =>
        {
            await LoadOrderAsync(employees);
            if (IsLoaded)
            {
                _orderRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                _orderRefreshTimer.Tick += async (_, _) => await RefreshOrderFromSyncAsync();
                _orderRefreshTimer.Start();
            }
        };
        Closed += (_, _) =>
        {
            _orderRefreshTimer?.Stop();
            RestaurantOrderNotifier.OrderChanged -= OnLocalOrderChanged;
        };
    }

    private void OnLocalOrderChanged(object? sender, EventArgs e)
    {
        DebugLog.Write("[RestaurantOrderWindow] OrderChanged recebido!");
        if (!IsLoaded)
        {
            DebugLog.Write("[RestaurantOrderWindow] Janela não carregada, ignorando.");
            return;
        }
        DebugLog.Write("[RestaurantOrderWindow] Agendando atualização da comanda...");
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            DebugLog.Write("[RestaurantOrderWindow] Atualizando comanda...");
            await RefreshOrderFromSyncAsync();
            DebugLog.Write("[RestaurantOrderWindow] Comanda atualizada.");
        }));
    }

    private async Task LoadOrderAsync(IReadOnlyList<RestaurantEmployeeRecord> employees)
    {
        try
        {
            MenuItemCombo.ItemsSource = await _restaurant.GetMenuItemsAsync(activeOnly: true);
            _order = await _restaurant.GetOpenOrderAsync(_table.Id);
            if (_order is null)
            {
                if (EmployeeCombo.Items.Count > 0)
                    EmployeeCombo.SelectedIndex = 0;
            }
            else
            {
                EmployeeCombo.SelectedItem = employees.FirstOrDefault(employee => employee.Name == _order.EmployeeName);
            }
            EmployeeCombo.IsEnabled = _order is null || _order.Items.Count == 0;
            await RefreshOrderAsync();
        }
        catch (Exception exception)
        {
            ShowError("Não foi possível abrir a comanda", exception);
            Close();
        }
    }

    private async Task RefreshOrderAsync(bool preservePaymentAmount = false)
    {
        var previousOrder = _order;
        var previousPaymentText = PaymentAmountBox.Text;
        _order = await _restaurant.GetOpenOrderAsync(_table.Id);
        if (_order is not null && previousOrder?.Id == _order.Id && _order.PaidTotal == 0)
            _order = _order with { WaiterServiceChargeEnabled = previousOrder.WaiterServiceChargeEnabled };
        if (_order is null)
        {
            _paymentSelections = [];
            TitleText.Text = $"Comanda • Mesa {_table.Number}";
            ItemsGrid.ItemsSource = null;
            WaiterServiceChargeCheckBox.IsChecked = false;
            WaiterServiceChargeCheckBox.IsEnabled = true;
            ServiceChargeText.Text = "10% do garçom: R$ 0,00";
            TotalText.Text = "Consumo: R$ 0,00";
            BalanceText.Text = "Saldo: R$ 0,00";
            PaymentAmountBox.Text = "0,00";
            StatusText.Text = "Mesa livre • adicione o primeiro item para abrir a comanda.";
            CustomerText.Text = "Cliente: não informado";
            return;
        }
        ItemsGrid.ItemsSource = RestaurantOrderDisplayGroup.Create(_order.Items);
        if (PayItemsCheckBox.IsChecked == true)
            UpdateItemPaymentSelection();
        TitleText.Text = _order.DailyNumber is null
            ? $"Comanda • Mesa {_table.Number}"
            : $"Comanda Nº {_order.DailyNumber} • Mesa {_table.Number}";
        EmployeeCombo.SelectedItem = _employees.FirstOrDefault(employee => employee.Name == _order.EmployeeName);
        EmployeeCombo.IsEnabled = _order.Items.Count == 0;
        WaiterServiceChargeCheckBox.IsChecked = _order.WaiterServiceChargeEnabled;
        WaiterServiceChargeCheckBox.IsEnabled = _order.PaidTotal == 0;
        ServiceChargeText.Text = $"10% do garçom: {_order.WaiterServiceCharge:C2}";
        TotalText.Text = $"Consumo: {_order.Subtotal:C2} • Total: {_order.Total:C2}";
        BalanceText.Text = $"Saldo: {_order.RemainingTotal:C2}";
        var paymentMatchesPreviousBalance = previousOrder is not null &&
            RestaurantEntryWindow.TryReadAmount(previousPaymentText, out var enteredAmount) &&
            enteredAmount == previousOrder.RemainingTotal;
        if (PayItemsCheckBox.IsChecked != true && (!preservePaymentAmount || paymentMatchesPreviousBalance))
            PaymentAmountBox.Text = _order.RemainingTotal.ToString("N2", System.Globalization.CultureInfo.CurrentCulture);
        StatusText.Text = string.IsNullOrWhiteSpace(_order.EmployeeName)
            ? $"Aberta em {_order.OpenedAt.ToLocalTime():dd/MM/yyyy HH:mm}"
            : $"Atendente: {_order.EmployeeName} • aberta em {_order.OpenedAt.ToLocalTime():HH:mm}";
        if (PayItemsCheckBox.IsChecked == true && _order.HasUnallocatedPayments)
            StatusText.Text = "Há pagamento sem vínculo com itens; desmarque Pagar por item e continue por valor.";
        CustomerText.Text = string.IsNullOrWhiteSpace(_order.CustomerName)
            ? "Cliente: não informado" : $"Cliente: {_order.CustomerName}";
    }

    private async Task RefreshOrderFromSyncAsync()
    {
        if (_refreshingOrder || !IsLoaded)
            return;
        _refreshingOrder = true;
        try
        {
            await RefreshOrderAsync(preservePaymentAmount: true);
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Falha ao atualizar a comanda: {exception.Message}";
        }
        finally
        {
            _refreshingOrder = false;
        }
    }

    private void WaiterServiceCharge_Click(object sender, RoutedEventArgs e)
    {
        if (_order is null)
            return;
        var previousBalance = _order.RemainingTotal;
        _order = _order with { WaiterServiceChargeEnabled = WaiterServiceChargeCheckBox.IsChecked == true };
        ServiceChargeText.Text = $"10% do garçom: {_order.WaiterServiceCharge:C2}";
        TotalText.Text = $"Consumo: {_order.Subtotal:C2} • Total: {_order.Total:C2}";
        BalanceText.Text = $"Saldo: {_order.RemainingTotal:C2}";
        if (RestaurantEntryWindow.TryReadAmount(PaymentAmountBox.Text, out var enteredAmount) &&
            enteredAmount == previousBalance)
            PaymentAmountBox.Text = _order.RemainingTotal.ToString("N2", System.Globalization.CultureInfo.CurrentCulture);
        if (PayItemsCheckBox.IsChecked == true)
            UpdateItemPaymentSelection();
    }

    private void UseFullBalance_Click(object sender, RoutedEventArgs e)
    {
        if (_order is not null && PayItemsCheckBox.IsChecked != true)
            PaymentAmountBox.Text = _order.RemainingTotal.ToString("N2", CultureInfo.CurrentCulture);
    }

    private void SplitBalance_Click(object sender, RoutedEventArgs e)
    {
        if (_order is null || PayItemsCheckBox.IsChecked == true)
            return;
        if (!int.TryParse(SplitPeopleBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var peopleCount) ||
            peopleCount is < 1 or > 100)
        {
            MessageBox.Show("Informe uma quantidade de pessoas entre 1 e 100.", "Quantidade inválida",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var share = decimal.Round(
            _order.RemainingTotal / peopleCount,
            2,
            MidpointRounding.AwayFromZero);
        PaymentAmountBox.Text = share.ToString("N2", CultureInfo.CurrentCulture);
    }

    private async void AddItem_Click(object sender, RoutedEventArgs e)
    {
        if (MenuItemCombo.SelectedItem is not RestaurantMenuItemRecord item)
        {
            MessageBox.Show("Cadastre e selecione um item ativo do cardápio.", "Item necessário",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!RestaurantEntryWindow.TryReadAmount(QuantityBox.Text, out var quantity) || quantity <= 0)
        {
            MessageBox.Show("Informe uma quantidade maior que zero.", "Quantidade inválida",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        IReadOnlyList<RestaurantOrderSelection>? selections = null;
        string? size = null;
        if (item.PriceM is not null || item.PriceG is not null)
        {
            if (item.PriceM is null)
                size = "G";
            else if (item.PriceG is null)
                size = "M";
            else
            {
                var choice = MessageBox.Show(
                    $"Escolha o tamanho de {item.Name}:\n\nSim: M — {item.PriceM:C2}\nNão: G — {item.PriceG:C2}",
                    "Tamanho", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (choice == MessageBoxResult.Cancel)
                    return;
                size = choice == MessageBoxResult.Yes ? "M" : "G";
            }
        }
        if (item.Options is { Count: > 0 } itemOptions)
        {
            var dialog = new OrderOptionsWindow(item.Name, quantity, itemOptions) { Owner = this };
            if (dialog.ShowDialog() != true)
                return;
            selections = dialog.Selections;
        }
        try
        {
            if (size is null)
                await _restaurant.AddOrderItemAsync(
                    _table.Id, item.Id, quantity, (EmployeeCombo.SelectedItem as RestaurantEmployeeRecord)?.Id,
                    ObservationBox.Text, selections);
            else
                await _restaurant.AddSizedOrderItemAsync(
                    _table.Id, item.Id, quantity, (EmployeeCombo.SelectedItem as RestaurantEmployeeRecord)?.Id,
                    ObservationBox.Text, selections, size);
            QuantityBox.Text = "1";
            ObservationBox.Clear();
            await RefreshOrderAsync();
        }
        catch (Exception exception)
        {
            ShowError("Não foi possível adicionar o item", exception);
            return;
        }
        if (_kitchen is { Enabled: true })
        {
            await _kitchen.ProcessPendingAsync();
            var failed = (await _kitchen.GetFailedAsync()).FirstOrDefault(batch => batch.TableNumber == _table.Number);
            if (failed is not null)
            {
                StatusText.Text = "⚠ Item lançado, mas a impressão automática falhou. Consulte as falhas de impressão.";
                MessageBox.Show(
                    $"O item foi lançado, mas a impressão automática falhou:\n\n{failed.LastError}\n\nUse a opção de reenviar falhas na tela do restaurante.",
                    "Falha na impressão da cozinha", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private async void RemoveOrderItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RestaurantOrderDisplayGroup group })
            return;
        RestaurantOrderItemRecord item;
        if (group.Items.Count == 1)
            item = group.Items[0];
        else
        {
            var choices = group.Items.Select((entry, index) => new
            {
                Item = entry,
                Label = $"Lançamento {index + 1}: {entry.Quantity:N2} × {entry.Name} ({entry.Total:C2})"
            }).ToArray();
            var list = new ComboBox { ItemsSource = choices, DisplayMemberPath = "Label", SelectedIndex = 0, Margin = new Thickness(0, 8, 0, 12) };
            var confirm = new Button { Content = "Continuar", Padding = new Thickness(12, 6, 12, 6), IsDefault = true };
            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(new TextBlock { Text = "Escolha o lançamento original. A linha inteira escolhida será removida.", TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(list);
            panel.Children.Add(confirm);
            var dialog = new Window
            {
                Title = "Selecionar lançamento",
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                SizeToContent = SizeToContent.WidthAndHeight,
                MinWidth = 440,
                ResizeMode = ResizeMode.NoResize,
                Content = panel
            };
            confirm.Click += (_, _) => dialog.DialogResult = true;
            if (dialog.ShowDialog() != true || list.SelectedIndex < 0)
                return;
            item = choices[list.SelectedIndex].Item;
        }
        if (MessageBox.Show(
                $"Remover a linha inteira de {item.Quantity:N2} × {item.Name} ({item.Total:C2}) desta comanda?\n\nSe o item estiver vinculado ao estoque, a quantidade será devolvida.\n\nEsta ação não imprime aviso à cozinha ou ao balcão. Avise a equipe pessoalmente.",
                "Confirmar remoção do item",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        try
        {
            await _restaurant.RemoveOrderItemAsync(_table.Id, item.Id);
            await RefreshOrderAsync();
        }
        catch (Exception exception)
        {
            ShowError("Não foi possível remover o item da comanda", exception);
        }
    }

    private void PaymentCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
            return;
        var isCash = PaymentCombo.SelectedIndex == 0;
        ChangePanel.Visibility = isCash ? Visibility.Visible : Visibility.Collapsed;
        if (isCash)
        {
            ReceivedAmountBox.Text = PaymentAmountBox.Text;
            UpdateChange();
        }
    }

    private void PayItems_Click(object sender, RoutedEventArgs e)
    {
        if (PayItemsCheckBox.IsChecked == true && (_order is null || _order.HasUnallocatedPayments))
        {
            PayItemsCheckBox.IsChecked = false;
            MessageBox.Show(_order is null ? "Abra uma comanda antes de selecionar itens." :
                "Há pagamento por valor sem vínculo com itens nesta comanda. Continue recebendo por valor.",
                "Pagamento por item indisponível", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        var enabled = PayItemsCheckBox.IsChecked == true;
        SelectPaymentItemsButton.IsEnabled = enabled;
        PaymentAmountBox.IsReadOnly = enabled;
        UseFullBalanceButton.IsEnabled = !enabled;
        SplitBalanceButton.IsEnabled = !enabled;
        _paymentSelections = [];
        PaymentAmountBox.Text = enabled ? "0,00" : (_order?.RemainingTotal ?? 0m).ToString("N2", CultureInfo.CurrentCulture);
        if (enabled)
            SelectPaymentItems_Click(sender, e);
    }

    private void SelectPaymentItems_Click(object sender, RoutedEventArgs e)
    {
        if (_order is null)
            return;
        if (_order.HasUnallocatedPayments)
        {
            MessageBox.Show("Há pagamento por valor sem vínculo com itens. Desmarque Pagar por item e continue por valor.",
                "Pagamento por item indisponível", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new RestaurantItemSelectionWindow(_order, unpaidOnly: true) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;
        _paymentSelections = dialog.Selections;
        UpdateItemPaymentSelection();
    }

    private void UpdateItemPaymentSelection()
    {
        if (_order is null)
            return;
        if (_order.HasUnallocatedPayments || _paymentSelections.Any(selection =>
                _order.Items.FirstOrDefault(item => item.Id == selection.ItemId) is not { } item ||
                selection.Quantity > item.RemainingQuantity))
        {
            _paymentSelections = [];
            StatusText.Text = "A seleção de pagamento mudou; selecione os itens novamente.";
        }
        var amount = _paymentSelections.Count == 0 ? 0m : _order.ItemPaymentAmount(_paymentSelections);
        PaymentAmountBox.Text = amount.ToString("N2", CultureInfo.CurrentCulture);
    }

    private void ReceivedAmount_TextChanged(object sender, TextChangedEventArgs e) => UpdateChange();

    private void PaymentAmount_TextChanged(object sender, TextChangedEventArgs e) => UpdateChange();

    private void UpdateChange()
    {
        if (ChangeText is null || ReceivedAmountBox is null || PaymentAmountBox is null)
            return;
        if (_order is null ||
            !RestaurantEntryWindow.TryReadAmount(ReceivedAmountBox.Text, out var received) ||
            !RestaurantEntryWindow.TryReadAmount(PaymentAmountBox.Text, out var paymentAmount) ||
            paymentAmount <= 0)
        {
            ChangeText.Text = "R$ 0,00";
            return;
        }
        var change = received - paymentAmount;
        ChangeText.Text = change >= 0 ? change.ToString("C2") : $"Faltam {(-change):C2}";
        ChangeText.Foreground = change >= 0
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(24, 169, 153))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(180, 35, 24));
    }

    private async void Pay_Click(object sender, RoutedEventArgs e)
    {
        if (_order is null || _order.Items.Count == 0)
        {
            MessageBox.Show("Adicione itens à comanda antes de receber.", "Comanda vazia",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!RestaurantEntryWindow.TryReadAmount(PaymentAmountBox.Text, out var amount) || amount <= 0)
        {
            MessageBox.Show("Informe um valor de pagamento maior que zero.", "Valor inválido",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var method = PaymentCombo.SelectedIndex switch
        {
            1 => PaymentMethod.Pix,
            2 => PaymentMethod.DebitCard,
            3 => PaymentMethod.CreditCard,
            _ => PaymentMethod.Cash
        };
        try
        {
            if (sender is Button button)
                button.IsEnabled = false;
            var payment = PayItemsCheckBox.IsChecked == true
                ? await _restaurant.PayOrderItemsAsync(_table.Id, method, _paymentSelections,
                    WaiterServiceChargeCheckBox.IsChecked == true, amount)
                : await _restaurant.PayOrderAsync(_table.Id, method, amount,
                    WaiterServiceChargeCheckBox.IsChecked == true);
            _paymentSelections = [];
            if (payment.RemainingTotal > 0)
            {
                await RefreshOrderAsync();
                MessageBox.Show(
                    $"Pagamento de {payment.Amount:C2} registrado. Saldo restante: {payment.RemainingTotal:C2}.",
                    "Pagamento parcial", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            MessageBox.Show($"Pagamento de {payment.Amount:C2} registrado. A mesa está livre novamente.",
                "Comanda encerrada", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            ShowError("Não foi possível receber o pagamento", exception);
        }
        finally
        {
            if (sender is Button button)
                button.IsEnabled = true;
        }
    }

    private async void PrintOrder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_kitchen is null)
                throw new InvalidOperationException("O serviço de impressão não está disponível.");
            await RefreshOrderAsync(preservePaymentAmount: true);
            if (_order is not { Items.Count: > 0 } order)
            {
                MessageBox.Show("Adicione itens à comanda antes de imprimir.", "Comanda vazia",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            IReadOnlyList<RestaurantItemPaymentSelection> selections;
            if (PrintScopeCombo.SelectedIndex == 1)
            {
                var dialog = new RestaurantItemSelectionWindow(order, unpaidOnly: false) { Owner = this };
                if (dialog.ShowDialog() != true)
                    return;
                selections = dialog.Selections;
            }
            else
            {
                selections = order.Items.Select(item => new RestaurantItemPaymentSelection(item.Id, item.Quantity)).ToArray();
            }
            var quantities = selections.ToDictionary(selection => selection.ItemId, selection => selection.Quantity);
            await RefreshOrderAsync(preservePaymentAmount: true);
            if (_order is null || _order.Id != order.Id ||
                selections.Any(selection => _order.Items.FirstOrDefault(item => item.Id == selection.ItemId) is not { } item ||
                    selection.Quantity > item.Quantity))
                throw new InvalidOperationException("A comanda mudou durante a seleção. Atualize e tente imprimir novamente.");
            order = _order;
            var batches = await _restaurant.GetAllKitchenBatchesForTableAsync(_table.Id);
            var batchItems = batches.Where(batch => batch.OrderId == order.Id)
                .SelectMany(batch => batch.Items).ToDictionary(item => item.Id);
            if (selections.Any(selection => !batchItems.TryGetValue(selection.ItemId, out var item) ||
                    selection.Quantity > item.Quantity))
                throw new InvalidOperationException("A comanda mudou durante a seleção. Atualize e tente imprimir novamente.");
            if (PrintDestinationCombo.SelectedIndex == 0)
            {
                var copies = batches.Where(batch => batch.OrderId == order.Id)
                    .Select(batch => batch with
                    {
                        Items = batch.Items.Where(item => quantities.ContainsKey(item.Id))
                            .Select(item => item with { Quantity = quantities[item.Id] }).ToArray()
                    }).Where(batch => batch.Items.Count > 0).ToArray();
                await _kitchen.PrintKitchenCopiesAsync(order, copies);
            }
            else
            {
                var selectedItems = order.Items.Where(item => quantities.ContainsKey(item.Id))
                    .Select(item => item with { Quantity = quantities[item.Id] }).ToArray();
                var area = batches.First(batch => batch.OrderId == order.Id).AreaName;
                await _kitchen.PrintCounterCheckAsync(order, selectedItems, _table.Number, area);
            }
        }
        catch (Exception exception)
        {
            ShowError("Não foi possível imprimir a comanda", exception);
        }
    }

    private static void ShowError(string title, Exception exception) =>
        MessageBox.Show($"{title}.\n\n{exception.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
}
