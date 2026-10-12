using System.Windows;
using SYSMR.Application.Abstractions;
using SYSMR.Infrastructure.Persistence;

namespace SYSMR.UI;

public partial class StoreAccountCreationWindow : Window
{
    private readonly FirebaseStoreAccountClient _firebase;
    private readonly IAuditLogRepository _auditLog;

    public StoreAccountCreationWindow(
        FirebaseStoreAccountClient firebase,
        IAuditLogRepository auditLog)
    {
        InitializeComponent();
        _firebase = firebase;
        _auditLog = auditLog;
    }

    public StoreAccountIdentity? Account { get; private set; }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = string.Empty;
        if (!PasswordBox.Password.Equals(ConfirmPasswordBox.Password, StringComparison.Ordinal))
        {
            StatusText.Text = "As senhas não coincidem.";
            return;
        }

        CreateButton.IsEnabled = false;
        try
        {
            var number = CustomerNumberBox.Text.Trim();
            var name = CustomerNameBox.Text.Trim();
            var password = PasswordBox.Password;
            var storeType = (StoreTypeCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string;
            if (string.IsNullOrWhiteSpace(storeType))
                throw new InvalidOperationException("Selecione o tipo de operação da loja.");
            Account = await _firebase.CreateAccountAsync(
                number,
                name,
                password,
                SuperAdminEmailBox.Text.Trim(),
                SuperAdminPasswordBox.Password,
                storeType);
            await _auditLog.RecordAsync("Super Admin", "Conta de loja criada", $"Conta {Account.CustomerNumber} cadastrada.");
            DialogResult = true;
        }
        catch (Exception exception)
        {
            StatusText.Text = Account is null
                ? $"Não foi possível criar a conta: {exception.Message}"
                : $"A conta {Account.CustomerNumber} foi criada no Firebase, mas não foi possível gravar a auditoria: {exception.Message}";
        }
        finally
        {
            SuperAdminPasswordBox.Clear();
            CreateButton.IsEnabled = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
