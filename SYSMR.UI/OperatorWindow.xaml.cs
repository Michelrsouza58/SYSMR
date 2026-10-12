using System.Windows;
using System.Windows.Input;

namespace SYSMR.UI;

public partial class OperatorWindow : Window
{
    public OperatorWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => SellerNameBox.Focus();
    }

    public string SellerName => SellerNameBox.Text.Trim();

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SellerName))
        {
            MessageBox.Show("Informe o nome do vendedor para iniciar o turno.", "SYSMR", MessageBoxButton.OK, MessageBoxImage.Warning);
            SellerNameBox.Focus();
            return;
        }
        DialogResult = true;
    }

    private void SellerNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Start_Click(sender, new RoutedEventArgs());
        }
    }
}
