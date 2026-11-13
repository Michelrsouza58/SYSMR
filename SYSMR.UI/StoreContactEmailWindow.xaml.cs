using System.Windows;
using SYSMR.Application.Abstractions;
using SYSMR.Infrastructure.Persistence;

namespace SYSMR.UI;

public partial class StoreContactEmailWindow : Window
{
    private readonly FirebaseStoreAccountClient _firebase;
    private readonly IAuditLogRepository _auditLog;

    public StoreContactEmailWindow(
        FirebaseStoreAccountClient firebase,
        IAuditLogRepository auditLog,
        string customerNumber = "")
    {
        InitializeComponent();
        _firebase = firebase;
        _auditLog = auditLog;
        CustomerNumberBox.Text = customerNumber;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = string.Empty;
        SaveButton.IsEnabled = false;
        try
        {
            var number = CustomerNumberBox.Text.Trim();
            var contactEmail = ContactEmailBox.Text.Trim();
            await _firebase.UpdateStoreContactEmailAsync(
                number,
                string.IsNullOrWhiteSpace(contactEmail) ? null : contactEmail,
                SuperAdminEmailBox.Text.Trim(),
                SuperAdminPasswordBox.Password);
            await _auditLog.RecordAsync(
                "Super Admin",
                "E-mail de contato da loja atualizado",
                string.IsNullOrWhiteSpace(contactEmail)
                    ? $"Conta {number}: e-mail de contato removido."
                    : $"Conta {number}: e-mail de contato definido.");
            StatusText.Text = string.IsNullOrWhiteSpace(contactEmail)
                ? "E-mail de contato removido. A loja voltou ao acesso interno."
                : "E-mail de contato salvo. A recuperação de senha será enviada para ele.";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Não foi possível salvar o e-mail: {exception.Message}";
        }
        finally
        {
            SuperAdminPasswordBox.Clear();
            SaveButton.IsEnabled = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
