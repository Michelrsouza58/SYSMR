using System.Windows;
using SYSMR.Application.Abstractions;
using SYSMR.Infrastructure.Persistence;

namespace SYSMR.UI;

public partial class StoreContactEmailLocalWindow : Window
{
    private readonly FirebaseStoreAccountClient _firebase;
    private readonly IAuditLogRepository _auditLog;
    private readonly string _storeUid;

    public StoreContactEmailLocalWindow(
        FirebaseStoreAccountClient firebase,
        IAuditLogRepository auditLog,
        string storeUid)
    {
        InitializeComponent();
        _firebase = firebase;
        _auditLog = auditLog;
        _storeUid = storeUid;
        StoreUidBox.Text = storeUid;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = string.Empty;
        SaveButton.IsEnabled = false;
        try
        {
            var contactEmail = ContactEmailBox.Text.Trim();
            await _firebase.SetStoreContactEmailByUidAsync(
                _storeUid,
                string.IsNullOrWhiteSpace(contactEmail) ? null : contactEmail,
                LocalPinBox.Password);
            await _auditLog.RecordAsync(
                "Super Admin",
                "E-mail de contato da loja atualizado (local)",
                string.IsNullOrWhiteSpace(contactEmail)
                    ? $"Loja {_storeUid}: e-mail de contato removido."
                    : $"Loja {_storeUid}: e-mail de contato definido.");
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
            LocalPinBox.Clear();
            SaveButton.IsEnabled = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
