using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using SYSMR.Application.Abstractions;
using SYSMR.Infrastructure.Persistence;

namespace SYSMR.UI;

public partial class StoreAccountLoginWindow : Window
{
    private readonly SqliteAccessControl _accessControl;
    private readonly SqliteStoreAccountRepository _localAccounts;
    private readonly FirebaseStoreAccountClient _firebase;
    private readonly IAuditLogRepository _auditLog;

    public StoreAccountLoginWindow(
        SqliteAccessControl accessControl,
        SqliteStoreAccountRepository localAccounts,
        FirebaseStoreAccountClient firebase,
        IAuditLogRepository auditLog)
    {
        InitializeComponent();
        _accessControl = accessControl;
        _localAccounts = localAccounts;
        _firebase = firebase;
        _auditLog = auditLog;
        Loaded += LoadAccountStateAsync;
        Loaded += (_, _) => CustomerNumberBox.Focus();
    }

    public StoreAccountIdentity? Account { get; private set; }
    public bool IsSuperAdminLogin { get; private set; }

    private async void Login_Click(object sender, RoutedEventArgs e) => await LoginAsync();

    private async Task LoginAsync()
    {
        StatusText.Text = string.Empty;
        var number = CustomerNumberBox.Text.Trim();
        var name = CustomerNameBox.Text.Trim();
        var password = PasswordBox.Password;
        try
        {
            Account = await _firebase.SignInAsync(number, name, password);
            await _localAccounts.SaveAuthenticatedAccountAsync(
                number, name, password, Account.FirebaseUid, Account.StoreType, Account.FirebaseRefreshToken);
            DialogResult = true;
        }
        catch (FirebaseAccountRequestException exception) when (exception.IsInvalidCredentials)
        {
            StatusText.Text = "Número, nome ou senha incorretos. Confira os dados e tente novamente.";
            PasswordBox.Clear();
            PasswordBox.Focus();
        }
        catch (HttpRequestException exception) when (exception.StatusCode is null || (int)exception.StatusCode >= 500)
        {
            await TryOfflineLoginAsync(number, name, password, exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            var cached = await _localAccounts.VerifyOfflineAsync(number, name, password);
            if (cached is null)
                StatusText.Text = exception.Message;
            else
            {
                Account = cached;
                await _localAccounts.EnableAutoLoginAsync();
                StatusText.Text = "Firebase não configurado; sessão iniciada offline.";
                DialogResult = true;
            }
        }
        catch (HttpRequestException exception)
        {
            StatusText.Text = $"Falha na comunicação com o Firebase: {exception.Message}";
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
    }

    private async Task TryOfflineLoginAsync(string number, string name, string password, string onlineError)
    {
        try
        {
            var cached = await _localAccounts.VerifyOfflineAsync(number, name, password);
            if (cached is null)
            {
                StatusText.Text = $"Firebase indisponível e não há credencial local válida. {onlineError}";
                return;
            }

            Account = cached;
            await _localAccounts.EnableAutoLoginAsync();
            StatusText.Text = "Firebase indisponível; sessão iniciada com a credencial local.";
            DialogResult = true;
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Não foi possível validar o acesso offline: {exception.Message}";
        }
    }

    private async void LoadAccountStateAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            CreateAccountButton.IsEnabled = !await _localAccounts.HasAccountAsync();
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Não foi possível verificar a conta local: {exception.Message}";
        }
    }

    private async void AdminLogin_Click(object sender, RoutedEventArgs e)
    {
        AdminStatusText.Text = string.Empty;
        try
        {
            if (!await _accessControl.VerifyAdministratorPinAsync(AdminPinBox.Password))
            {
                AdminStatusText.Text = "PIN do Super Admin incorreto.";
                AdminPinBox.Clear();
                AdminPinBox.Focus();
                return;
            }

            Account = null;
            IsSuperAdminLogin = true;
            DialogResult = true;
        }
        catch (Exception exception)
        {
            AdminStatusText.Text = $"Não foi possível validar o Super Admin: {exception.Message}";
        }
    }

    private void LoginTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded || e.Source != LoginTabs)
            return;

        if (LoginTabs.SelectedIndex == 0)
            CustomerNumberBox.Focus();
        else
            AdminPinBox.Focus();
    }

    private void AdminPinBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            AdminLogin_Click(sender, new RoutedEventArgs());
        }
    }

    private async void ConfigureFirebase_Click(object sender, RoutedEventArgs e)
    {
        if (!await AuthorizeSuperAdminAsync())
            return;
        var dialog = new FirebaseConfigWindow(_accessControl, _auditLog) { Owner = this };
        dialog.ShowDialog();
    }

    private async void ForgotPassword_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = string.Empty;
        var number = CustomerNumberBox.Text.Trim();
        var name = CustomerNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(number) || string.IsNullOrWhiteSpace(name))
        {
            StatusText.Text = "Informe o número e o nome da loja para recuperar a senha.";
            return;
        }

        try
        {
            await _firebase.SendPasswordResetAsync(number, name);
            StatusText.Text = "Enviamos um link de redefinição para o e-mail de contato cadastrado na loja. " +
                              "Verifique a caixa de entrada e o spam.";
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
    }

    private async void ContactEmail_Click(object sender, RoutedEventArgs e)
    {
        if (!await AuthorizeSuperAdminAsync())
            return;
        var dialog = new StoreContactEmailWindow(_firebase, _auditLog, CustomerNumberBox.Text.Trim()) { Owner = this };
        dialog.ShowDialog();
    }

    private async void ContactEmailLocal_Click(object sender, RoutedEventArgs e)
    {
        if (!await AuthorizeSuperAdminAsync())
            return;
        var account = await _localAccounts.GetRememberedAccountAsync();
        if (account is null)
        {
            StatusText.Text = "Nenhuma loja está vinculada a este computador.";
            return;
        }
        var dialog = new StoreContactEmailLocalWindow(_firebase, _auditLog, account.FirebaseUid) { Owner = this };
        dialog.ShowDialog();
    }

    private async void UnlinkAccount_Click(object sender, RoutedEventArgs e)
    {
        if (!await AuthorizeSuperAdminAsync())
            return;
        
        var result = MessageBox.Show(
            "Desvincular a loja remove a conta local e permite criar ou vincular outra loja.\n\n" +
            "Deseja também APAGAR TODOS os dados locais (produtos, vendas, estoque, comandas)?\n\n" +
            "• SIM = Apaga tudo e começa do zero\n" +
            "• NÃO = Mantém os dados locais (serão visíveis na nova loja)\n" +
            "• CANCELAR = Não faz nada",
            "Desvincular loja",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);
        
        if (result == MessageBoxResult.Cancel)
            return;
        
        var clearData = result == MessageBoxResult.Yes;

        try
        {
            await _localAccounts.RemoveAccountAsync(clearData);
            await _auditLog.RecordAsync("Super Admin", "Loja desvinculada", 
                clearData ? "Conta removida e dados locais apagados." : "Conta removida, dados locais mantidos.");
            StatusText.Text = clearData 
                ? "Loja desvinculada e dados apagados. Crie uma nova conta." 
                : "Loja desvinculada. Os dados locais serão visíveis na nova conta.";
            CreateAccountButton.IsEnabled = true;
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Não foi possível desvincular: {exception.Message}";
        }
    }

    private async void CreateAccount_Click(object sender, RoutedEventArgs e)
    {
        if (await _localAccounts.HasAccountAsync())
        {
            StatusText.Text = "Este computador já está vinculado a uma loja. Use outro banco local para cadastrar outra loja.";
            return;
        }
        if (!await AuthorizeSuperAdminAsync())
            return;

        var dialog = new StoreAccountCreationWindow(_firebase, _auditLog)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
        {
            CustomerNumberBox.Text = dialog.Account!.CustomerNumber;
            CustomerNameBox.Text = dialog.Account.CustomerName;
            PasswordBox.Clear();
            StatusText.Text = "Conta criada. Agora informe a senha da loja e selecione Entrar para acessar o SYSMR.";
            PasswordBox.Focus();
        }
    }

    private Task<bool> AuthorizeSuperAdminAsync()
    {
        var authorization = new AdminPinWindow(_accessControl, initialSetup: false) { Owner = this };
        return Task.FromResult(authorization.ShowDialog() == true);
    }

    private async void PasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await LoginAsync();
        }
    }
}
