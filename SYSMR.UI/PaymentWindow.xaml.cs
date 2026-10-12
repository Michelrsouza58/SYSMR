using System.Windows;
using System.Windows.Controls;
using SYSMR.Domain.Entities;

namespace SYSMR.UI;

public partial class PaymentWindow : Window
{
    public PaymentWindow(decimal total, IReadOnlyList<string> sellers)
    {
        InitializeComponent();
        PaymentTotal.Text = total.ToString("C2");
        SellerComboBox.ItemsSource = sellers;
        if (sellers.Count > 0)
            SellerComboBox.SelectedIndex = 0;
        else
        {
            SellerComboBox.IsEnabled = false;
            SellerHint.Text = "Cadastre vendedores em Configurações antes de finalizar uma venda.";
            SellerHint.Visibility = Visibility.Visible;
        }
    }

    public PaymentMethod SelectedPayment { get; private set; }
    public string SellerName => SellerComboBox.SelectedItem as string ?? string.Empty;

    private void SellerComboBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            e.Handled = true;
            PaymentCombo.Focus();
        }
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SellerName))
        {
            MessageBox.Show("Selecione um vendedor cadastrado. Se a lista estiver vazia, cadastre-o nas Configurações.",
                "Vendedor obrigatório",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            SellerComboBox.Focus();
            return;
        }

        SelectedPayment = PaymentCombo.SelectedIndex switch
        {
            1 => PaymentMethod.Pix,
            2 => PaymentMethod.DebitCard,
            3 => PaymentMethod.CreditCard,
            _ => PaymentMethod.Cash
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
