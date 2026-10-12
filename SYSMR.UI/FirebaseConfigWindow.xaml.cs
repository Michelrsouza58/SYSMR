using System.Windows;
using SYSMR.Application.Abstractions;
using SYSMR.Infrastructure.Persistence;

namespace SYSMR.UI;

public partial class FirebaseConfigWindow : Window
{
    private readonly SqliteAccessControl _settings;
    private readonly IAuditLogRepository _auditLog;

    public FirebaseConfigWindow(SqliteAccessControl settings, IAuditLogRepository auditLog)
    {
        InitializeComponent();
        _settings = settings;
        _auditLog = auditLog;
        Loaded += LoadSettingsAsync;
    }

    private async void LoadSettingsAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            ProjectIdBox.Text = await _settings.GetSettingAsync("firebase.projectId") ?? string.Empty;
            ApiKeyBox.Text = await _settings.GetSettingAsync("firebase.webApiKey") ?? string.Empty;
            DatabaseUrlBox.Text = await _settings.GetSettingAsync("firebase.databaseUrl") ?? string.Empty;
            ProvisioningUrlBox.Text = await _settings.GetSettingAsync("firebase.provisioningUrl") ?? string.Empty;
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Não foi possível carregar os parâmetros: {exception.Message}";
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var databaseUrl = DatabaseUrlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(ProjectIdBox.Text) ||
            string.IsNullOrWhiteSpace(ApiKeyBox.Text) ||
            !Uri.TryCreate(databaseUrl, UriKind.Absolute, out var parsedUrl) ||
            parsedUrl.Scheme != Uri.UriSchemeHttps ||
            !Uri.TryCreate(ProvisioningUrlBox.Text.Trim(), UriKind.Absolute, out var provisioningUrl) ||
            provisioningUrl.Scheme != Uri.UriSchemeHttps)
        {
            StatusText.Text = "Informe Project ID, Web API Key, Realtime Database URL e URL HTTPS da Cloud Function.";
            return;
        }

        try
        {
            await _settings.SetSettingAsync("firebase.projectId", ProjectIdBox.Text.Trim());
            await _settings.SetSettingAsync("firebase.webApiKey", ApiKeyBox.Text.Trim());
            await _settings.SetSettingAsync("firebase.databaseUrl", databaseUrl.TrimEnd('/'));
            await _settings.SetSettingAsync("firebase.provisioningUrl", provisioningUrl.ToString());
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Não foi possível salvar: {exception.Message}";
            return;
        }

        try
        {
            await _auditLog.RecordAsync(
                "Super Admin",
                "Firebase configurado",
                $"Project ID: {ProjectIdBox.Text.Trim()}; Database URL: {databaseUrl.TrimEnd('/')}.");
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Os parâmetros foram salvos, mas falhou o registro na auditoria: {exception.Message}";
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
