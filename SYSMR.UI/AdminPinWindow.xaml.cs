using System.Windows;
using SYSMR.Infrastructure.Persistence;

namespace SYSMR.UI;

public partial class AdminPinWindow : Window
{
    private readonly SqliteAccessControl _accessControl;
    private readonly bool _initialSetup;

    public AdminPinWindow(SqliteAccessControl accessControl, bool initialSetup)
    {
        InitializeComponent();
        _accessControl = accessControl;
        _initialSetup = initialSetup;
        if (!initialSetup)
        {
            TitleText.Text = "Autorização necessária";
            ExplanationText.Text = "Informe o PIN do Super Admin para acessar esta área protegida.";
            ConfirmPanel.Visibility = Visibility.Collapsed;
            SubmitButton.Content = "Autorizar";
            CancelButton.Visibility = Visibility.Visible;
        }
        else
        {
            CancelButton.Visibility = Visibility.Collapsed;
        }
        Loaded += (_, _) => PinBox.Focus();
    }

    private async void Submit_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;
        try
        {
            if (_initialSetup)
            {
                if (PinBox.Password != ConfirmPinBox.Password)
                {
                    ErrorText.Text = "Os PINs não coincidem.";
                    return;
                }
                await _accessControl.ConfigureAdministratorAsync(PinBox.Password);
                DialogResult = true;
                return;
            }

            if (await _accessControl.VerifyAdministratorPinAsync(PinBox.Password))
            {
                DialogResult = true;
                return;
            }
            ErrorText.Text = "PIN incorreto.";
            PinBox.Clear();
            PinBox.Focus();
        }
        catch (Exception exception)
        {
            ErrorText.Text = exception.Message;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
