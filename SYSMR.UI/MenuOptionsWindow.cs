using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using SYSMR.Application.Models;

namespace SYSMR.UI;

public sealed class MenuOptionsWindow : Window
{
    private readonly TextBox _editor;
    private readonly IReadOnlyList<RestaurantMenuOptionRecord> _original;

    public MenuOptionsWindow(string itemName, IReadOnlyList<RestaurantMenuOptionRecord> current)
    {
        _original = current;
        Title = $"Variações de {itemName}";
        Width = 520;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new DockPanel { Margin = new Thickness(18) };
        var help = new TextBlock
        {
            Text = "Uma variação por linha, no formato: Nome ; valor extra por unidade.\n" +
                   "Ex.: Borda de sal ; 0\nCom gelo e limão ; 1,50\n" +
                   "O valor pode ser alterado no momento do pedido.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };
        DockPanel.SetDock(help, Dock.Top);
        root.Children.Add(help);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var save = new Button { Content = "Salvar", Padding = new Thickness(18, 7, 18, 7), IsDefault = true };
        var cancel = new Button { Content = "Cancelar", Padding = new Thickness(18, 7, 18, 7), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        save.Click += Save_Click;
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        _editor = new TextBox
        {
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            TextWrapping = TextWrapping.NoWrap,
            Text = string.Join(Environment.NewLine,
                current.Select(option => $"{option.Name} ; {option.ExtraPrice.ToString("0.00", CultureInfo.CurrentCulture)}"))
        };
        root.Children.Add(_editor);
        Content = root;
    }

    public IReadOnlyList<RestaurantMenuOptionRecord> Options { get; private set; } = [];

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var result = new List<RestaurantMenuOptionRecord>();
        var lineNumber = 0;
        foreach (var rawLine in _editor.Text.Split('\n'))
        {
            lineNumber++;
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;
            var parts = line.Split(';', 2, StringSplitOptions.TrimEntries);
            var extra = 0m;
            if (parts[0].Length == 0 ||
                (parts.Length == 2 && parts[1].Length > 0 &&
                 (!RestaurantEntryWindow.TryReadAmount(parts[1], out extra) || extra < 0)))
            {
                MessageBox.Show(this, $"Linha {lineNumber} inválida. Use: Nome ; valor extra.", "Variações",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var existing = _original.FirstOrDefault(option =>
                option.Name.Equals(parts[0], StringComparison.CurrentCultureIgnoreCase));
            result.Add(new RestaurantMenuOptionRecord(existing?.Id ?? Guid.NewGuid(), parts[0], extra));
        }
        Options = result;
        DialogResult = true;
    }
}
