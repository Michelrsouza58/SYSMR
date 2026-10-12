using System.Globalization;
using System.Windows;

namespace SYSMR.UI;

public partial class RestaurantEntryWindow : Window
{
    private readonly int _fieldCount;
    private readonly bool _allowEmpty;
    private readonly IReadOnlySet<int>? _optionalFieldIndexes;

    public RestaurantEntryWindow(
        string title,
        IReadOnlyList<string> labels,
        bool allowEmpty = false,
        IReadOnlySet<int>? optionalFieldIndexes = null)
    {
        InitializeComponent();
        if (labels.Count is < 1 or > 5)
            throw new ArgumentOutOfRangeException(nameof(labels));
        TitleText.Text = title;
        _fieldCount = labels.Count;
        _allowEmpty = allowEmpty;
        _optionalFieldIndexes = optionalFieldIndexes;
        LabelOne.Text = labels[0];
        if (_fieldCount > 1)
        {
            LabelTwo.Text = labels[1];
        }
        else
        {
            LabelTwo.Visibility = Visibility.Collapsed;
            ValueTwo.Visibility = Visibility.Collapsed;
        }
        if (_fieldCount > 2)
        {
            LabelThree.Text = labels[2];
        }
        else
        {
            LabelThree.Visibility = Visibility.Collapsed;
            ValueThree.Visibility = Visibility.Collapsed;
        }
        if (_fieldCount > 3)
            LabelFour.Text = labels[3];
        else
        {
            LabelFour.Visibility = Visibility.Collapsed;
            ValueFour.Visibility = Visibility.Collapsed;
        }
        if (_fieldCount > 4)
            LabelFive.Text = labels[4];
        else
        {
            LabelFive.Visibility = Visibility.Collapsed;
            ValueFive.Visibility = Visibility.Collapsed;
        }
        Loaded += (_, _) => ValueOne.Focus();
    }

    public IReadOnlyList<string> Values { get; private set; } = [];

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        var values = new[]
            {
                ValueOne.Text.Trim(), ValueTwo.Text.Trim(), ValueThree.Text.Trim(),
                ValueFour.Text.Trim(), ValueFive.Text.Trim()
            }
            .Take(_fieldCount)
            .ToArray();
        if (!_allowEmpty && values.Where((value, index) => _optionalFieldIndexes?.Contains(index) != true)
                .Any(string.IsNullOrWhiteSpace))
        {
            MessageBox.Show("Preencha todos os campos.", "Dados obrigatórios",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (values.Any(value => value.Length > 100))
        {
            MessageBox.Show("Cada campo pode conter no máximo 100 caracteres.", "Valor muito longo",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Values = values;
        DialogResult = true;
    }

    public static bool TryReadAmount(string value, out decimal amount) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.CurrentCulture, out amount) ||
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out amount);

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
