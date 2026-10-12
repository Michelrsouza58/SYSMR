using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Globalization;
using SYSMR.Application.Models;
using SYSMR.Domain.Entities;
using SYSMR.Infrastructure.Persistence;

namespace SYSMR.UI;

public partial class RestaurantOrderWindow : Window
{
    private readonly SqliteRestaurantRepository _restaurant;
    private readonly RestaurantTableRecord _table;
    private RestaurantOrderRecord? _order;

    public RestaurantOrderWindow(
        SqliteRestaurantRepository restaurant,
        RestaurantTableRecord table,
        IReadOnlyList<RestaurantEmployeeRecord> employees)
    {
        InitializeComponent();
        _restaurant = restaurant;
        _table = table;
        TitleText.Text = $"Comanda • Mesa {table.Number}";
        EmployeeCombo.ItemsSource = employees;
        Loaded += async (_, _) => await LoadOrderAsync(employees);
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

    private async Task RefreshOrderAsync()
    {
        _order = await _restaurant.GetOpenOrderAsync(_table.Id);
        if (_order is null)
        {
            ItemsGrid.ItemsSource = null;
            WaiterServiceChargeCheckBox.IsChecked = false;
            WaiterServiceChargeCheckBox.IsEnabled = true;
            ServiceChargeText.Text = "10% do garçom: R$ 0,00";
            TotalText.Text = "Consumo: R$ 0,00";
            BalanceText.Text = "Saldo: R$ 0,00";
            PaymentAmountBox.Text = "0,00";
            StatusText.Text = "Mesa livre • adicione o primeiro item para abrir a comanda.";
            return;
        }
        ItemsGrid.ItemsSource = _order.Items;
        EmployeeCombo.IsEnabled = _order.Items.Count == 0;
        WaiterServiceChargeCheckBox.IsChecked = _order.WaiterServiceChargeEnabled;
        WaiterServiceChargeCheckBox.IsEnabled = _order.PaidTotal == 0;
        ServiceChargeText.Text = $"10% do garçom: {_order.WaiterServiceCharge:C2}";
        TotalText.Text = $"Consumo: {_order.Subtotal:C2} • Total: {_order.Total:C2}";
        BalanceText.Text = $"Saldo: {_order.RemainingTotal:C2}";
        PaymentAmountBox.Text = _order.RemainingTotal.ToString("N2", System.Globalization.CultureInfo.CurrentCulture);
        StatusText.Text = string.IsNullOrWhiteSpace(_order.EmployeeName)
            ? $"Aberta em {_order.OpenedAt.ToLocalTime():dd/MM/yyyy HH:mm}"
            : $"Atendente: {_order.EmployeeName} • aberta em {_order.OpenedAt.ToLocalTime():HH:mm}";
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
    }

    private void UseFullBalance_Click(object sender, RoutedEventArgs e)
    {
        if (_order is not null)
            PaymentAmountBox.Text = _order.RemainingTotal.ToString("N2", CultureInfo.CurrentCulture);
    }

    private void SplitBalance_Click(object sender, RoutedEventArgs e)
    {
        if (_order is null)
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
        try
        {
            await _restaurant.AddOrderItemAsync(
                _table.Id,
                item.Id,
                quantity,
                (EmployeeCombo.SelectedItem as RestaurantEmployeeRecord)?.Id);
            QuantityBox.Text = "1";
            await RefreshOrderAsync();
        }
        catch (Exception exception)
        {
            ShowError("Não foi possível adicionar o item", exception);
        }
    }

    private async void RemoveOrderItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RestaurantOrderItemRecord item })
            return;
        if (MessageBox.Show(
                $"Remover {item.Quantity:N2} × {item.Name} ({item.Total:C2}) desta comanda?\n\nSe o item estiver vinculado ao estoque, a quantidade será devolvida.",
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
            var payment = await _restaurant.PayOrderAsync(
                _table.Id,
                method,
                amount,
                WaiterServiceChargeCheckBox.IsChecked == true);
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
    }

    private void PrintKitchen_Click(object sender, RoutedEventArgs e)
    {
        if (_order is not { Items.Count: > 0 })
        {
            MessageBox.Show("Adicione itens à comanda antes de imprimir o pedido.", "Comanda vazia",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var printDialog = new PrintDialog();
            if (printDialog.ShowDialog() != true)
                return;

            var printableWidth = printDialog.PrintableAreaWidth > 0
                ? printDialog.PrintableAreaWidth
                : 280;
            var pageHeight = printDialog.PrintableAreaHeight > 0
                ? Math.Min(printDialog.PrintableAreaHeight, Math.Max(300, 260 + 54 * _order.Items.Count))
                : Math.Max(300, 260 + 54 * _order.Items.Count);
            var ink = new SolidColorBrush(Color.FromRgb(23, 35, 59));
            var accent = new SolidColorBrush(Color.FromRgb(24, 169, 153));
            var muted = new SolidColorBrush(Color.FromRgb(104, 119, 143));
            var separator = new SolidColorBrush(Color.FromRgb(222, 228, 236));
            var document = new FlowDocument
            {
                PagePadding = new Thickness(22, 18, 22, 18),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12,
                PageWidth = printableWidth,
                PageHeight = pageHeight,
                ColumnWidth = Math.Max(120, printableWidth - 44),
                TextAlignment = TextAlignment.Left
            };

            var brand = new Paragraph(new Run("SYSMR"))
            {
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = accent,
                Margin = new Thickness(0, 0, 0, 3),
                TextAlignment = TextAlignment.Center
            };
            document.Blocks.Add(brand);
            document.Blocks.Add(new Paragraph(new Run("PEDIDO DE COZINHA"))
            {
                FontSize = 17,
                FontWeight = FontWeights.Bold,
                Foreground = ink,
                Margin = new Thickness(0, 0, 0, 12),
                TextAlignment = TextAlignment.Center
            });

            var summary = new Table { CellSpacing = 0 };
            summary.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
            summary.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
            var summaryRow = new TableRow();
            summaryRow.Cells.Add(CreateTicketCell("MESA", $"Nº {_table.Number}", ink, accent, TextAlignment.Left));
            summaryRow.Cells.Add(CreateTicketCell(
                "HORÁRIO",
                _order.OpenedAt.ToLocalTime().ToString("dd/MM/yyyy  HH:mm"),
                ink,
                accent,
                TextAlignment.Right));
            summary.RowGroups.Add(new TableRowGroup { Rows = { summaryRow } });
            document.Blocks.Add(summary);
            document.Blocks.Add(new Paragraph(new Run(
                $"ATENDENTE  {(_order.EmployeeName.Length == 0 ? "Não informado" : _order.EmployeeName)}"))
            {
                FontSize = 10,
                Foreground = muted,
                Margin = new Thickness(0, 8, 0, 14),
                TextAlignment = TextAlignment.Center
            });

            var sectionTitle = new Paragraph(new Run("ITENS DO PEDIDO"))
            {
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = ink,
                BorderBrush = separator,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 0, 0, 6),
                Margin = new Thickness(0, 0, 0, 5)
            };
            document.Blocks.Add(sectionTitle);

            var itemsTable = new Table { CellSpacing = 0 };
            itemsTable.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
            itemsTable.Columns.Add(new TableColumn { Width = new GridLength(48) });
            var itemRows = new TableRowGroup();
            foreach (var item in _order.Items)
            {
                var row = new TableRow();
                row.Cells.Add(CreateTicketCell(item.Name, null, ink, muted, TextAlignment.Left));
                row.Cells.Add(CreateTicketCell(
                    "× " + item.Quantity.ToString("0.##", CultureInfo.GetCultureInfo("pt-BR")),
                    null,
                    ink,
                    muted,
                    TextAlignment.Right));
                itemRows.Rows.Add(row);
            }
            itemsTable.RowGroups.Add(itemRows);
            document.Blocks.Add(itemsTable);
            document.Blocks.Add(new Paragraph(new Run(
                $"{_order.Items.Sum(item => item.Quantity):0.##} unidade(s)  •  { _order.Items.Count} linha(s)"))
            {
                FontSize = 9,
                Foreground = muted,
                BorderBrush = separator,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(0, 8, 0, 0),
                Margin = new Thickness(0, 8, 0, 0),
                TextAlignment = TextAlignment.Center
            });
            document.Blocks.Add(new Paragraph(new Run("Preparar e conferir antes de entregar."))
            {
                FontSize = 9,
                Foreground = muted,
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(0, 10, 0, 0),
                TextAlignment = TextAlignment.Center
            });

            printDialog.PrintDocument(
                ((IDocumentPaginatorSource)document).DocumentPaginator,
                "Pedido de cozinha SYSMR");
        }
        catch (Exception exception)
        {
            ShowError("Não foi possível imprimir o pedido da cozinha", exception);
        }
    }

    private static TableCell CreateTicketCell(
        string primaryText,
        string? secondaryText,
        Brush primaryBrush,
        Brush secondaryBrush,
        TextAlignment alignment)
    {
        var cell = new TableCell
        {
            Padding = new Thickness(4, 6, 4, 6),
            TextAlignment = alignment,
            BorderBrush = new SolidColorBrush(Color.FromRgb(236, 239, 244)),
            BorderThickness = new Thickness(0, 0, 0, 0.5)
        };
        if (secondaryText is not null)
        {
            cell.Blocks.Add(new Paragraph(new Run(primaryText))
            {
                FontSize = 8,
                FontWeight = FontWeights.Bold,
                Foreground = secondaryBrush,
                Margin = new Thickness(0, 0, 0, 2),
                TextAlignment = alignment
            });
            cell.Blocks.Add(new Paragraph(new Run(secondaryText))
            {
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = primaryBrush,
                Margin = new Thickness(0),
                TextAlignment = alignment
            });
        }
        else
        {
            cell.Blocks.Add(new Paragraph(new Run(primaryText))
            {
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = primaryBrush,
                Margin = new Thickness(0),
                TextAlignment = alignment
            });
        }
        return cell;
    }

    private static void ShowError(string title, Exception exception) =>
        MessageBox.Show($"{title}.\n\n{exception.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
}
