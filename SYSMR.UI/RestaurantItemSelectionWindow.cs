using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using SYSMR.Application.Models;

namespace SYSMR.UI;

internal sealed class RestaurantItemSelectionWindow : Window
{
    private readonly List<(RestaurantOrderItemRecord Item, CheckBox Selected, TextBox Quantity)> _rows = [];
    private readonly bool _unpaidOnly;
    public IReadOnlyList<RestaurantItemPaymentSelection> Selections { get; private set; } = [];

    public RestaurantItemSelectionWindow(RestaurantOrderRecord order, bool unpaidOnly)
    {
        _unpaidOnly = unpaidOnly;
        Title = unpaidOnly ? "Selecionar itens para pagamento" : "Selecionar itens para impressão";
        Width = 680;
        Height = 520;
        MinWidth = 540;
        MinHeight = 350;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(18) };
        var heading = new TextBlock
        {
            Text = unpaidOnly
                ? "Marque os lançamentos e informe a quantidade que será paga."
                : "Marque os lançamentos e informe a quantidade que deseja imprimir.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        };
        DockPanel.SetDock(heading, Dock.Top);
        root.Children.Add(heading);
        var confirm = new Button { Content = "Confirmar seleção", Padding = new Thickness(14, 8, 14, 8),
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), IsDefault = true };
        confirm.Click += Confirm_Click;
        DockPanel.SetDock(confirm, Dock.Bottom);
        root.Children.Add(confirm);
        var panel = new StackPanel();
        foreach (var item in order.Items)
        {
            var available = unpaidOnly ? item.RemainingQuantity : item.Quantity;
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            var check = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
                IsEnabled = available > 0 };
            row.Children.Add(check);
            var label = new TextBlock
            {
                Text = $"{item.Name} — {item.UnitPrice:C2}\nTotal: {FormatQuantity(item.Quantity)} • Pago por item: {FormatQuantity(item.PaidQuantity)} • Disponível: {FormatQuantity(available)}" +
                    (string.IsNullOrWhiteSpace(item.Observation) ? "" : $"\nObs.: {item.Observation}"),
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetColumn(label, 1);
            row.Children.Add(label);
            var quantity = new TextBox { Text = FormatQuantity(available),
                Padding = new Thickness(6), VerticalAlignment = VerticalAlignment.Center, IsEnabled = available > 0 };
            Grid.SetColumn(quantity, 2);
            row.Children.Add(quantity);
            panel.Children.Add(row);
            _rows.Add((item, check, quantity));
        }
        root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        var selected = new List<RestaurantItemPaymentSelection>();
        foreach (var (item, check, quantityBox) in _rows)
        {
            if (check.IsChecked != true)
                continue;
            var available = _unpaidOnly ? item.RemainingQuantity : item.Quantity;
            if (!RestaurantEntryWindow.TryReadAmount(quantityBox.Text, out var quantity) || quantity <= 0 || quantity > available)
            {
                MessageBox.Show($"Informe uma quantidade maior que zero e até {FormatQuantity(available)} para {item.Name}.",
                    "Quantidade inválida", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            selected.Add(new RestaurantItemPaymentSelection(item.Id, quantity));
        }
        if (selected.Count == 0)
        {
            MessageBox.Show("Selecione pelo menos um item.", "Seleção vazia", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Selections = selected;
        DialogResult = true;
    }

    private static string FormatQuantity(decimal value) =>
        value.ToString("0.############################", CultureInfo.CurrentCulture);
}
