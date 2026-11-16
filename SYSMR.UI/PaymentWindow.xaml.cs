using System.Windows;
using System.Windows.Controls;
using SYSMR.Domain.Entities;

namespace SYSMR.UI;

public partial class PaymentWindow : Window
{
    private readonly decimal _total;
    private readonly IReadOnlyList<decimal> _rates;
    public PaymentWindow(decimal total, IReadOnlyList<string> sellers, IReadOnlyList<decimal> rates, bool hasRegisteredCustomer = false)
    {
        _total = total;
        _rates = rates;
        InitializeComponent();
        InstallmentsCombo.ItemsSource = Enumerable.Range(1, 12).Select(i => $"{i}x").ToArray();
        InstallmentsCombo.SelectedIndex = 0;
        PaymentTotal.Text = total.ToString("C2");
        StoreCreditItem.IsEnabled = hasRegisteredCustomer;
        StoreCreditHint.Text = hasRegisteredCustomer
            ? "A venda em conta exige um cliente cadastrado e não entra no caixa até o recebimento."
            : "Selecione um cliente cadastrado no PDV para liberar a opção de conta.";
        StoreCreditHint.Visibility = Visibility.Visible;
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
    public CreditTerms? SelectedCreditTerms { get; private set; }
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
            4 when StoreCreditItem.IsEnabled => PaymentMethod.StoreCredit,
            _ => PaymentMethod.Cash
        };
        if (PaymentCombo.SelectedIndex == 4 && !StoreCreditItem.IsEnabled)
        {
            MessageBox.Show("Selecione um cliente cadastrado no PDV antes de abrir uma conta.",
                "Cliente obrigatório", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        SelectedCreditTerms = SelectedPayment == PaymentMethod.CreditCard
            ? new CreditTerms(InstallmentsCombo.SelectedIndex + 1, _rates[InstallmentsCombo.SelectedIndex])
            : null;
        if (SelectedCreditTerms is not null &&
            SelectedCreditTerms.CalculateMinimumTotal(_total) < SelectedCreditTerms.Installments / 100m)
        {
            MessageBox.Show("Escolha menos parcelas: cada parcela deve ser de pelo menos R$ 0,01.",
                "Valor insuficiente", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }

    private void UpdateCredit(object sender, SelectionChangedEventArgs e)
    {
        if (CreditPanel is null || InstallmentsCombo is null || CreditSummary is null || PaymentTotal is null)
            return;
        var credit = PaymentCombo.SelectedIndex == 3;
        CreditPanel.Visibility = credit ? Visibility.Visible : Visibility.Collapsed;
        if (!credit || InstallmentsCombo.SelectedIndex < 0)
        {
            PaymentTotal.Text = _total.ToString("C2");
            return;
        }
        var terms = new CreditTerms(InstallmentsCombo.SelectedIndex + 1, _rates[InstallmentsCombo.SelectedIndex]);
        var total = terms.CalculateTotal(_total);
        PaymentTotal.Text = total.ToString("C2");
        CreditSummary.Text = InstallmentDisplay.Format(terms, _total, total);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
