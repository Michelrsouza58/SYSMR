using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using SYSMR.Domain.Entities;
using SYSMR.Application.Models;

namespace SYSMR.UI;

public partial class AccountPaymentWindow : Window
{
    public AccountPaymentWindow(OpenAccountEntry account)
    {
        InitializeComponent();
        AccountCustomerText.Text = $"{account.Customer} • nota de {account.CreatedAt.ToLocalTime():dd/MM/yyyy}";
        AccountBalanceText.Text = $"Saldo em aberto: {account.BalanceDue:C2}";
        AmountBox.Text = account.BalanceDue.ToString("0.00", CultureInfo.CurrentCulture);
        AmountBox.SelectAll();
        AmountBox.Focus();
    }

    public decimal Amount { get; private set; }
    public PaymentMethod SelectedPayment { get; private set; }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (!decimal.TryParse(AmountBox.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var amount) ||
            amount <= 0 || amount != decimal.Round(amount, 2, MidpointRounding.AwayFromZero))
        {
            MessageBox.Show("Informe um valor positivo com até duas casas decimais.",
                "Valor inválido", MessageBoxButton.OK, MessageBoxImage.Warning);
            AmountBox.Focus();
            AmountBox.SelectAll();
            return;
        }

        Amount = amount;
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
