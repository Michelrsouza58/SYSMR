using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using SYSMR.Application.Models;

namespace SYSMR.UI;

public sealed class OrderOptionsWindow : Window
{
    private readonly decimal _total;
    private readonly List<(RestaurantMenuOptionRecord Option, TextBox Quantity, TextBox Extra)> _rows = [];

    public OrderOptionsWindow(string itemName, decimal total, IReadOnlyList<RestaurantMenuOptionRecord> options)
    {
        _total = total;
        Title = $"Variações - {itemName}";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock
        {
            Text = $"Total: {total:N2}. Informe quantos itens levam cada variação (o restante sai simples). O extra por unidade pode ser alterado.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        AddCell(grid, new TextBlock { Text = "Variação", FontWeight = FontWeights.SemiBold }, 0, 0);
        AddCell(grid, new TextBlock { Text = "Qtd.", FontWeight = FontWeights.SemiBold }, 0, 1);
        AddCell(grid, new TextBlock { Text = "Extra/un.", FontWeight = FontWeights.SemiBold }, 0, 2);

        for (var index = 0; index < options.Count; index++)
        {
            var option = options[index];
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var quantity = new TextBox { Text = "0", Padding = new Thickness(6), Margin = new Thickness(4) };
            var extra = new TextBox
            {
                Text = option.ExtraPrice.ToString("0.00", CultureInfo.CurrentCulture),
                Padding = new Thickness(6),
                Margin = new Thickness(4)
            };
            AddCell(grid, new TextBlock { Text = option.Name, VerticalAlignment = VerticalAlignment.Center }, index + 1, 0);
            AddCell(grid, quantity, index + 1, 1);
            AddCell(grid, extra, index + 1, 2);
            _rows.Add((option, quantity, extra));
        }
        grid.RowDefinitions.Insert(0, new RowDefinition { Height = GridLength.Auto });
        foreach (UIElement child in grid.Children)
            Grid.SetRow(child, Grid.GetRow(child));
        root.Children.Add(grid);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var ok = new Button { Content = "Adicionar", Padding = new Thickness(18, 7, 18, 7), IsDefault = true };
        var cancel = new Button { Content = "Cancelar", Padding = new Thickness(18, 7, 18, 7), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        ok.Click += Ok_Click;
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    public IReadOnlyList<RestaurantOrderSelection> Selections { get; private set; } = [];

    private static void AddCell(Grid grid, UIElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var selections = new List<RestaurantOrderSelection>();
        decimal sum = 0;
        foreach (var (option, quantityBox, extraBox) in _rows)
        {
            if (!RestaurantEntryWindow.TryReadAmount(quantityBox.Text, out var quantity) || quantity < 0 ||
                !RestaurantEntryWindow.TryReadAmount(extraBox.Text, out var extra) || extra < 0 || extra > 9999)
            {
                MessageBox.Show(this, $"Valores inválidos em \"{option.Name}\".", "Variações",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (quantity == 0)
                continue;
            sum += quantity;
            selections.Add(new RestaurantOrderSelection(option.Id, quantity, extra));
        }
        if (sum > _total)
        {
            MessageBox.Show(this, "A soma das variações excede a quantidade do item.", "Variações",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Selections = selections;
        DialogResult = true;
    }
}
