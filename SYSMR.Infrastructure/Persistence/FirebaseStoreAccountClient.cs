using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SYSMR.Infrastructure.Persistence;

public sealed class FirebaseStoreAccountClient(SqliteAccessControl settings)
{
    private static readonly HttpClient Http = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<StoreAccountIdentity> CreateAccountAsync(
        string customerNumber,
        string customerName,
        string password,
        string superAdminEmail,
        string superAdminPassword,
        string storeType = "sales",
        CancellationToken cancellationToken = default)
    {
        ValidateAccount(customerNumber, customerName, password);
        var config = await GetConfigurationAsync(cancellationToken);
        var normalizedNumber = SqliteStoreAccountRepository.NormalizeNumber(customerNumber);
        var normalizedName = customerName.Trim();
        storeType = NormalizeStoreType(storeType);
        if (string.IsNullOrWhiteSpace(superAdminEmail) || string.IsNullOrEmpty(superAdminPassword))
            throw new ArgumentException("Informe as credenciais Firebase do Super Admin.");
        if (!Uri.TryCreate(config.ProvisioningUrl, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Configure a URL HTTPS da função de provisionamento do Firebase.");

        var adminToken = await SignInWithEmailAsync(config.ApiKey, superAdminEmail.Trim(), superAdminPassword, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new
            {
                customerNumber = normalizedNumber,
                customerName = normalizedName,
                password,
                storeType
            }, options: JsonOptions)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken.IdToken);
        using var response = await Http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw FirebaseAccountRequestException.FromResponse(response.StatusCode, body);

        var created = JsonSerializer.Deserialize<ProvisionedAccount>(body, JsonOptions);
        if (created is null || string.IsNullOrWhiteSpace(created.Uid))
            throw new InvalidOperationException("A função Firebase retornou um identificador de conta inválido.");
        return new StoreAccountIdentity(normalizedNumber, normalizedName, created.Uid, storeType);
    }

    public async Task<StoreAccountIdentity> SignInAsync(
        string customerNumber,
        string customerName,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(customerNumber) || string.IsNullOrWhiteSpace(customerName))
            throw new ArgumentException("Informe o número e o nome da loja.");
        if (password.Length == 0)
            throw new ArgumentException("Informe a senha da loja.", nameof(password));

        var config = await GetConfigurationAsync(cancellationToken);
        var normalizedNumber = SqliteStoreAccountRepository.NormalizeNumber(customerNumber);
        var token = await SignInWithEmailAsync(config.ApiKey, CreateLoginEmail(normalizedNumber), password, cancellationToken);
        var profile = await ReadProfileAsync(config.DatabaseUrl, token, cancellationToken);
        if (!profile.CustomerNumber.Equals(normalizedNumber, StringComparison.OrdinalIgnoreCase) ||
            !profile.CustomerName.Equals(customerName.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("O número ou nome não corresponde à conta Firebase.");

        return new StoreAccountIdentity(
            profile.CustomerNumber,
            profile.CustomerName,
            token.LocalId,
            NormalizeStoreType(profile.StoreType ?? "sales"),
            token.RefreshToken);
    }

    public async Task<FirebaseSession> RefreshSessionAsync(
        string firebaseUid,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        var config = await GetConfigurationAsync(cancellationToken);
        using var response = await Http.PostAsync(
            $"https://securetoken.googleapis.com/v1/token?key={Uri.EscapeDataString(config.ApiKey)}",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken
            }),
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw FirebaseAccountRequestException.FromResponse(response.StatusCode, body);

        var refreshed = JsonSerializer.Deserialize<RefreshedToken>(body, JsonOptions);
        if (refreshed is not { UserId.Length: > 0, IdToken.Length: > 0, RefreshToken.Length: > 0 } ||
            !refreshed.UserId.Equals(firebaseUid, StringComparison.Ordinal))
            throw new InvalidOperationException("O Firebase retornou uma sessão renovada inválida ou de outra loja.");
        return new FirebaseSession(refreshed.IdToken, refreshed.RefreshToken, config.DatabaseUrl);
    }

    public async Task<string> UploadOutboxEventAsync(
        string firebaseUid,
        string branch,
        string eventId,
        string payload,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        if (branch is not ("sales" or "restaurant"))
            throw new ArgumentException("A vertente da sincronização é inválida.", nameof(branch));
        using var document = JsonDocument.Parse(payload);
        var session = await RefreshSessionAsync(firebaseUid, refreshToken, cancellationToken);
        var endpoint = $"{session.DatabaseUrl}/clients/{Uri.EscapeDataString(firebaseUid)}/branches/{branch}/events/{Uri.EscapeDataString(eventId)}.json?auth={Uri.EscapeDataString(session.IdToken)}";
        using var requestContent = new StringContent(document.RootElement.GetRawText(), Encoding.UTF8, "application/json");
        using var response = await Http.PutAsync(endpoint, requestContent, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Não foi possível sincronizar evento com o Realtime Database ({(int)response.StatusCode}): {body}",
                null,
                response.StatusCode);
        return session.RefreshToken;
    }

    public async Task<string> ChangeStoreTypeAsync(
        string firebaseUid,
        string refreshToken,
        string storeType,
        CancellationToken cancellationToken = default)
    {
        storeType = NormalizeStoreType(storeType);
        var session = await RefreshSessionAsync(firebaseUid, refreshToken, cancellationToken);
        var endpoint = $"{session.DatabaseUrl}/clients/{Uri.EscapeDataString(firebaseUid)}/storeType.json?auth={Uri.EscapeDataString(session.IdToken)}";
        using var content = JsonContent.Create(storeType, options: JsonOptions);
        using var response = await Http.PutAsync(endpoint, content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Não foi possível alterar a vertente da loja no Firebase ({(int)response.StatusCode}): {body}",
                null,
                response.StatusCode);
        return session.RefreshToken;
    }

    public static string CreateLoginEmail(string customerNumber)
    {
        var normalized = SqliteStoreAccountRepository.NormalizeNumber(customerNumber);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return $"store-{Convert.ToHexString(digest).ToLowerInvariant()}@accounts.sysmr.app";
    }

    private async Task<FirebaseConfiguration> GetConfigurationAsync(CancellationToken cancellationToken)
    {
        var apiKey = await settings.GetSettingAsync("firebase.webApiKey", cancellationToken);
        var databaseUrl = await settings.GetSettingAsync("firebase.databaseUrl", cancellationToken);
        var provisioningUrl = await settings.GetSettingAsync("firebase.provisioningUrl", cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey) ||
            !Uri.TryCreate(databaseUrl, UriKind.Absolute, out var parsedUrl) ||
            parsedUrl.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException(
                "Configure a Web API Key e a URL HTTPS do Firebase antes de usar a autenticação online.");

        return new FirebaseConfiguration(apiKey, databaseUrl.TrimEnd('/'), provisioningUrl ?? string.Empty);
    }

    private static async Task<AuthToken> SignInWithEmailAsync(
        string apiKey,
        string email,
        string password,
        CancellationToken cancellationToken) =>
        await SendAuthRequestAsync(
            $"https://identitytoolkit.googleapis.com/v1/accounts:signInWithPassword?key={Uri.EscapeDataString(apiKey)}",
            new { email, password, returnSecureToken = true },
            cancellationToken);

    private static async Task<AuthToken> SendAuthRequestAsync(
        string url,
        object request,
        CancellationToken cancellationToken)
    {
        using var response = await Http.PostAsJsonAsync(url, request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if ((int)response.StatusCode >= 500)
            throw new HttpRequestException(
                $"Firebase Authentication indisponível ({(int)response.StatusCode}): {body}",
                null,
                response.StatusCode);
        if (!response.IsSuccessStatusCode)
            throw FirebaseAccountRequestException.FromResponse(response.StatusCode, body);

        var token = JsonSerializer.Deserialize<AuthToken>(body, JsonOptions);
        return token is { LocalId.Length: > 0, IdToken.Length: > 0 }
            ? token
            : throw new InvalidOperationException("O Firebase retornou uma resposta de autenticação inválida.");
    }

    private static async Task<StoreProfile> ReadProfileAsync(
        string databaseUrl,
        AuthToken token,
        CancellationToken cancellationToken)
    {
        var url = $"{databaseUrl}/clients/{Uri.EscapeDataString(token.LocalId)}.json?auth={Uri.EscapeDataString(token.IdToken)}";
        using var response = await Http.GetAsync(url, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Não foi possível consultar o perfil da loja no Realtime Database ({(int)response.StatusCode}): {body}",
                null,
                response.StatusCode);
        return JsonSerializer.Deserialize<StoreProfile>(body, JsonOptions) ??
               throw new InvalidOperationException("A conta autenticada não possui um perfil de loja no Firebase.");
    }

    private static void ValidateAccount(string customerNumber, string customerName, string password)
    {
        var number = SqliteStoreAccountRepository.NormalizeNumber(customerNumber);
        if (number.Length is < 3 or > 32 || number.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
            throw new ArgumentException("O número da loja deve ter de 3 a 32 caracteres: letras, números ou hífen.");
        if (string.IsNullOrWhiteSpace(customerName) || customerName.Trim().Length > 120)
            throw new ArgumentException("Informe o nome da loja (até 120 caracteres).", nameof(customerName));
        if (password.Length is < 8 or > 128)
            throw new ArgumentException("A senha deve ter entre 8 e 128 caracteres.", nameof(password));
    }

    private static string NormalizeStoreType(string storeType) =>
        storeType.Trim().ToLowerInvariant() switch
        {
            "restaurant" => "restaurant",
            "sales" => "sales",
            _ => throw new ArgumentException("Selecione uma vertente válida para a loja.", nameof(storeType))
        };

    private sealed record FirebaseConfiguration(string ApiKey, string DatabaseUrl, string ProvisioningUrl);
    private sealed record AuthToken(string LocalId, string IdToken, string RefreshToken);
    private sealed record RefreshedToken(
        [property: JsonPropertyName("user_id")] string UserId,
        [property: JsonPropertyName("id_token")] string IdToken,
        [property: JsonPropertyName("refresh_token")] string RefreshToken);
    private sealed record ProvisionedAccount(string Uid);
    private sealed record StoreProfile(string CustomerNumber, string CustomerName, string? StoreType = null);
}

public sealed record FirebaseSession(string IdToken, string RefreshToken, string DatabaseUrl);

public sealed class FirebaseAccountRequestException(
    string errorCode,
    HttpStatusCode statusCode,
    string message) : Exception(message)
{
    public string ErrorCode { get; } = errorCode;
    public HttpStatusCode StatusCode { get; } = statusCode;

    public bool IsInvalidCredentials =>
        ErrorCode is "INVALID_LOGIN_CREDENTIALS" or "EMAIL_NOT_FOUND" or "INVALID_PASSWORD" or "USER_DISABLED";

    public static FirebaseAccountRequestException FromResponse(HttpStatusCode statusCode, string body)
    {
        string code = "UNKNOWN";
        string message = "A solicitação de autenticação Firebase falhou.";
        try
        {
            using var document = JsonDocument.Parse(body);
            var error = document.RootElement.GetProperty("error");
            if (error.TryGetProperty("message", out var messageProperty))
            {
                var rawMessage = messageProperty.GetString() ?? message;
                code = rawMessage.Split(' ', ':')[0];
                message = rawMessage;
            }
        }
        catch (JsonException)
        {
            message = $"A solicitação Firebase falhou com HTTP {(int)statusCode}.";
        }
        catch (InvalidOperationException)
        {
            message = $"A solicitação Firebase falhou com HTTP {(int)statusCode}: {body}";
        }

        return new FirebaseAccountRequestException(code, statusCode, message);
    }
}
