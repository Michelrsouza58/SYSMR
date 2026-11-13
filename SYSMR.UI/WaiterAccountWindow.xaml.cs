using System.Windows;
using System.Windows.Controls;
using SYSMR.Application.Models;

namespace SYSMR.UI;

public partial class WaiterAccountWindow : Window
{
    private readonly bool _requiresPassword;

    public WaiterAccountWindow(
        RestaurantEmployeeRecord? employee,
        IReadOnlyList<RestaurantAreaRecord> areas)
    {
        InitializeComponent();
        _requiresPassword = employee?.HasWebAccess != true;
        TitleText.Text = employee is null
            ? "Cadastrar funcionário e acesso web"
            : _requiresPassword ? "Configurar acesso web" : "Editar funcionário e acesso web";
        PasswordPanel.Visibility = _requiresPassword ? Visibility.Visible : Visibility.Collapsed;
        NameBox.Text = employee?.Name ?? string.Empty;
        RoleBox.Text = employee?.Role ?? "Garçom";
        NumberBox.Text = employee?.WaiterNumber ?? string.Empty;
        NumberBox.IsReadOnly = employee?.HasWebAccess == true;
        LocalPinHint.Text = employee is null
            ? "De 4 a 12 dígitos. Usado no app do garçom pela rede local, mesmo sem internet. Deixe em branco para não liberar o acesso local."
            : "De 4 a 12 dígitos. Usado no app do garçom pela rede local, mesmo sem internet. Deixe em branco para manter o PIN atual.";

        var selectedAreas = (employee?.AreaIds ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var area in areas)
        {
            AreasPanel.Children.Add(new CheckBox
            {
                Content = area.Name,
                Tag = area.Id,
                IsChecked = employee is null || selectedAreas.Contains(area.Id.ToString()),
                Margin = new Thickness(0, 4, 0, 4)
            });
        }
    }

    public string EmployeeName => NameBox.Text.Trim();
    public string EmployeeRole => RoleBox.Text.Trim();
    public string WaiterNumber => NumberBox.Text.Trim().ToUpperInvariant();
    public string InitialPassword => PasswordBox.Password;
    public string LocalPin => LocalPinBox.Password;
    public IReadOnlyList<Guid> SelectedAreaIds => AreasPanel.Children
        .OfType<CheckBox>()
        .Where(checkBox => checkBox.IsChecked == true && checkBox.Tag is Guid)
        .Select(checkBox => (Guid)checkBox.Tag)
        .ToArray();

    public void ClearPassword()
    {
        PasswordBox.Clear();
        ConfirmPasswordBox.Clear();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(EmployeeName) ||
            EmployeeName.Length > 80 ||
            string.IsNullOrWhiteSpace(EmployeeRole) ||
            string.IsNullOrWhiteSpace(WaiterNumber))
        {
            MessageBox.Show("Informe nome e função (até 80 caracteres) e o número do garçom.",
                "Dados obrigatórios", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (WaiterNumber.Length > 20 ||
            !WaiterNumber.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'))
        {
            MessageBox.Show("O número do garçom deve conter até 20 letras, números ou hífens.",
                "Número inválido", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (SelectedAreaIds.Count == 0)
        {
            MessageBox.Show("Selecione pelo menos uma área para este garçom.",
                "Área necessária", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_requiresPassword && PasswordBox.Password.Length < 8 && LocalPinBox.Password.Length == 0)
        {
            MessageBox.Show("Informe a senha inicial (mínimo 8 caracteres) para o acesso web ou um PIN local para o acesso pela rede Wi-Fi.",
                "Credencial necessária", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (PasswordBox.Password.Length > 0 && PasswordBox.Password.Length < 8)
        {
            MessageBox.Show("A senha inicial deve ter pelo menos 8 caracteres.",
                "Senha inválida", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (PasswordBox.Password.Length > 0 && !PasswordBox.Password.Equals(ConfirmPasswordBox.Password, StringComparison.Ordinal))
        {
            MessageBox.Show("As senhas não coincidem.", "Confirme a senha",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            ConfirmPasswordBox.Clear();
            ConfirmPasswordBox.Focus();
            return;
        }
        if (LocalPinBox.Password.Length > 0 &&
            (LocalPinBox.Password.Length < 4 || LocalPinBox.Password.Length > 12 ||
             !LocalPinBox.Password.All(char.IsAsciiDigit)))
        {
            MessageBox.Show("O PIN local deve ter de 4 a 12 dígitos numéricos.",
                "PIN inválido", MessageBoxButton.OK, MessageBoxImage.Warning);
            LocalPinBox.Clear();
            LocalPinBox.Focus();
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
