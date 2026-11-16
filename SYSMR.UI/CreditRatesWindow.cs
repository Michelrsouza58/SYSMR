using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using SYSMR.Application.Services;
using SYSMR.Infrastructure.Persistence;

namespace SYSMR.UI;

internal sealed class CreditRatesWindow : Window
{
    private readonly SqliteAccessControl _settings;
    private readonly List<TextBox> _fields = [];
    public const string SettingKey = "sales.creditRates";

    public static async Task<IReadOnlyList<decimal>> LoadAsync(SqliteAccessControl settings)
    {
        var stored = await settings.GetSettingAsync(SettingKey);
        if (stored is null)
            return CreditRates.InfinitePayDefaults;
        var rates = JsonSerializer.Deserialize<decimal[]>(stored)
            ?? throw new InvalidOperationException("As taxas de crédito salvas estão inválidas.");
        CreditRates.Validate(rates);
        return rates;
    }

    public CreditRatesWindow(SqliteAccessControl settings, IReadOnlyList<decimal> rates)
    {
        _settings = settings;
        Title = "Taxas de cartão de crédito";
        Width = 480;
        Height = 670;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = "Taxa descontada pela maquininha (%) por número de parcelas. Confira as taxas com seu contrato.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        });
        for (var i = 0; i < 12; i++)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new TextBlock { Text = $"{i + 1}x", Width = 70, VerticalAlignment = VerticalAlignment.Center });
            var field = new TextBox { Width = 120, Padding = new Thickness(5), Text = rates[i].ToString("0.############################", CultureInfo.CurrentCulture) };
            row.Children.Add(field);
            row.Children.Add(new TextBlock { Text = " %", VerticalAlignment = VerticalAlignment.Center });
            _fields.Add(field);
            panel.Children.Add(row);
        }
        var save = new Button { Content = "Salvar taxas", Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        save.Click += Save_Click;
        panel.Children.Add(save);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var rates = new List<decimal>();
        foreach (var field in _fields)
        {
            if (!RestaurantEntryWindow.TryReadAmount(field.Text, out var value) || value < 0 || value >= 100)
            {
                MessageBox.Show("Informe uma taxa válida entre 0% e menos de 100% em cada campo.", "Taxa inválida", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            rates.Add(value);
        }
        try
        {
            await _settings.SetSettingAsync(SettingKey, JsonSerializer.Serialize(rates));
            DialogResult = true;
        }
        catch (Exception exception)
        {
            MessageBox.Show($"Não foi possível salvar as taxas: {exception.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
