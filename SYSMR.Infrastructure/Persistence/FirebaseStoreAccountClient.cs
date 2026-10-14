using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SYSMR.Application.Models;

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

    public async Task<string> UploadRestaurantMenuAsync(
        string firebaseUid,
        IReadOnlyList<RestaurantMenuItemRecord> menuItems,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firebaseUid);
        ArgumentNullException.ThrowIfNull(menuItems);

        var categories = menuItems
            .Select(item => NormalizeMenuCategory(item.Category))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                CreateMenuCategoryId,
                category => new { id = CreateMenuCategoryId(category), name = category },
                StringComparer.Ordinal);
        var items = menuItems.ToDictionary(
            item => item.Id.ToString(),
            item => new
            {
                id = item.Id,
                name = item.Name,
                categoryId = CreateMenuCategoryId(NormalizeMenuCategory(item.Category)),
                price = item.Price,
                isActive = item.IsActive
            });
        var menu = new
        {
            schemaVersion = 1,
            updatedAt = DateTimeOffset.UtcNow,
            categories,
            items
        };

        var session = await RefreshSessionAsync(firebaseUid, refreshToken, cancellationToken);
        var endpoint = $"{session.DatabaseUrl}/clients/{Uri.EscapeDataString(firebaseUid)}/branches/restaurant/restaurantLive/menu.json?auth={Uri.EscapeDataString(session.IdToken)}";
        using var content = JsonContent.Create(menu, options: JsonOptions);
        using var response = await Http.PutAsync(endpoint, content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Não foi possível publicar o cardápio no Realtime Database ({(int)response.StatusCode}): {body}",
                null,
                response.StatusCode);
        return session.RefreshToken;
    }

    public async Task<string> UploadRestaurantAreasAsync(
        string firebaseUid,
        IReadOnlyList<RestaurantAreaRecord> areas,
        IReadOnlyDictionary<Guid, IReadOnlyList<RestaurantTableRecord>> tablesByArea,
        IReadOnlyDictionary<Guid, RestaurantOrderRecord?> openOrdersByTable,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firebaseUid);
        ArgumentNullException.ThrowIfNull(areas);
        ArgumentNullException.ThrowIfNull(tablesByArea);
        ArgumentNullException.ThrowIfNull(openOrdersByTable);

        var areaNodes = new Dictionary<string, object>();
        foreach (var area in areas)
        {
            var tableNodes = tablesByArea.TryGetValue(area.Id, out var tables)
                ? tables.ToDictionary(
                    table => table.Id.ToString(),
                    table =>
                    {
                        var order = openOrdersByTable.GetValueOrDefault(table.Id);
                        return (object)new
                        {
                            number = table.Number,
                            isActive = table.IsActive,
                            openOrder = order is null
                                ? null
                                : new
                                {
                                    id = order.Id,
                                    openedAt = order.OpenedAt,
                                    employeeName = order.EmployeeName,
                                    items = order.Items.Select(item => new
                                    {
                                        id = item.Id,
                                        name = item.Name,
                                        quantity = item.Quantity,
                                        unitPrice = item.UnitPrice,
                                        observation = item.Observation
                                    }),
                                    subtotal = order.Subtotal,
                                    waiterServiceChargeEnabled = order.WaiterServiceChargeEnabled,
                                    waiterServiceCharge = order.WaiterServiceCharge,
                                    total = order.Total,
                                    paidTotal = order.PaidTotal,
                                    remainingTotal = order.RemainingTotal
                                }
                        };
                    })
                : new Dictionary<string, object>();
            areaNodes.Add(
                area.Id.ToString(),
                new
                {
                    id = area.Id,
                    name = area.Name,
                    sortOrder = area.SortOrder,
                    isActive = area.IsActive,
                    tables = tableNodes
                });
        }
        var session = await RefreshSessionAsync(firebaseUid, refreshToken, cancellationToken);
        var endpoint = $"{session.DatabaseUrl}/clients/{Uri.EscapeDataString(firebaseUid)}/branches/restaurant/restaurantLive/areas.json?auth={Uri.EscapeDataString(session.IdToken)}";
        using var content = JsonContent.Create(areaNodes, options: JsonOptions);
        using var response = await Http.PutAsync(endpoint, content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Não foi possível publicar áreas e mesas no Realtime Database ({(int)response.StatusCode}): {body}",
                null,
                response.StatusCode);
        return session.RefreshToken;
    }

    public async Task<(string RefreshToken, IReadOnlyList<WaiterCommandRecord> Commands)> GetPendingWaiterCommandsAsync(
        string firebaseUid,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        var session = await RefreshSessionAsync(firebaseUid, refreshToken, cancellationToken);
        var endpoint = $"{session.DatabaseUrl}/clients/{Uri.EscapeDataString(firebaseUid)}/branches/restaurant/waiterCommands.json?auth={Uri.EscapeDataString(session.IdToken)}&orderBy=%22status%22&equalTo=%22pending%22";
        using var response = await Http.GetAsync(endpoint, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Não foi possível consultar pedidos dos garçons no Realtime Database ({(int)response.StatusCode}): {body}",
                null,
                response.StatusCode);

        using var document = JsonDocument.Parse(body);
        var commands = new List<WaiterCommandRecord>();
        if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var command = property.Value.Deserialize<WaiterCommandRecord>(JsonOptions);
                if (command is not null && command.Status?.Equals("pending", StringComparison.OrdinalIgnoreCase) == true)
                    commands.Add(command with { Id = command.Id ?? property.Name });
            }
        }
        else if (document.RootElement.ValueKind is not JsonValueKind.Null)
        {
            throw new InvalidDataException("A lista de pedidos dos garçons no Firebase tem formato inválido.");
        }

        return (session.RefreshToken, commands);
    }

    public async Task<string> CompleteWaiterCommandAsync(
        string firebaseUid,
        string commandId,
        WaiterCommandResult result,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        ArgumentNullException.ThrowIfNull(result);
        var session = await RefreshSessionAsync(firebaseUid, refreshToken, cancellationToken);
        var endpoint = $"{session.DatabaseUrl}/clients/{Uri.EscapeDataString(firebaseUid)}/branches/restaurant/waiterCommands/{Uri.EscapeDataString(commandId)}.json?auth={Uri.EscapeDataString(session.IdToken)}";
        using var content = JsonContent.Create(new
        {
            status = result.Status,
            result = new
            {
                message = result.Message,
                orderId = result.OrderId,
                processedAt = DateTimeOffset.UtcNow
            }
        }, options: JsonOptions);
        using var response = await Http.PatchAsync(endpoint, content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Não foi possível atualizar o resultado do pedido do garçom ({(int)response.StatusCode}): {body}",
                null,
                response.StatusCode);
        return session.RefreshToken;
    }

    public async Task<WaiterAccountProvisioningResult> CreateOrUpdateWaiterAccountAsync(
        StoreAccountIdentity store,
        string refreshToken,
        RestaurantEmployeeRecord employee,
        IReadOnlyCollection<Guid> areaIds,
        string? initialPassword,
        CancellationToken cancellationToken = default)
    {
        if (!store.StoreType.Equals("restaurant", StringComparison.Ordinal))
            throw new InvalidOperationException("O cadastro de garçons exige uma loja da vertente Restaurante.");
        if (string.IsNullOrWhiteSpace(employee.WaiterNumber))
            throw new ArgumentException("Informe o número do garçom.", nameof(employee));
        if (areaIds.Count == 0)
            throw new ArgumentException("Atribua pelo menos uma área ao garçom.", nameof(areaIds));

        var storeSlug = CreateWaiterStoreSlug(store.CustomerNumber);
        var waiterNumber = NormalizeWaiterNumber(employee.WaiterNumber);
        var waiterEmail = CreateWaiterLoginEmail(storeSlug, waiterNumber);
        string waiterUid;

        if (string.IsNullOrWhiteSpace(employee.FirebaseUid))
        {
            if (string.IsNullOrWhiteSpace(initialPassword) || initialPassword.Length is < 8 or > 128)
                throw new ArgumentException("A senha inicial deve ter entre 8 e 128 caracteres.", nameof(initialPassword));

            var config = await GetConfigurationAsync(cancellationToken);
            AuthToken waiterSession;
            try
            {
                waiterSession = await CreateAuthUserAsync(config.ApiKey, waiterEmail, initialPassword, cancellationToken);
            }
            catch (FirebaseAccountRequestException exception) when (exception.ErrorCode == "EMAIL_EXISTS")
            {
                waiterSession = await SignInWithEmailAsync(
                    config.ApiKey, waiterEmail, initialPassword, cancellationToken);
            }
            waiterUid = waiterSession.LocalId;
        }
        else
        {
            waiterUid = employee.FirebaseUid;
        }

        var storeSession = await RefreshSessionAsync(store.FirebaseUid, refreshToken, cancellationToken);
        var createdAt = DateTimeOffset.UtcNow.ToString("O");
        var directoryUrl = $"{storeSession.DatabaseUrl}/waiterDirectory/{Uri.EscapeDataString(waiterUid)}.json?auth={Uri.EscapeDataString(storeSession.IdToken)}";
        using (var directoryResponse = await Http.GetAsync(directoryUrl, cancellationToken))
        {
            var body = await directoryResponse.Content.ReadAsStringAsync(cancellationToken);
            if (directoryResponse.IsSuccessStatusCode)
            {
                using var directoryDocument = JsonDocument.Parse(body);
                if (directoryDocument.RootElement.ValueKind != JsonValueKind.Null)
                {
                    var existingDirectory = JsonSerializer.Deserialize<WaiterDirectoryProfile>(body, JsonOptions);
                    if (existingDirectory is null ||
                        !string.Equals(existingDirectory.StoreUid, store.FirebaseUid, StringComparison.Ordinal) ||
                        !string.Equals(existingDirectory.StoreSlug, storeSlug, StringComparison.Ordinal) ||
                        !string.Equals(existingDirectory.WaiterNumber, waiterNumber, StringComparison.Ordinal) ||
                        !string.Equals(existingDirectory.EmployeeId, employee.Id.ToString(), StringComparison.Ordinal))
                        throw new InvalidOperationException(
                            "Esse número já está vinculado a outra conta/funcionário. Não foi alterado nenhum cadastro.");
                    createdAt = string.IsNullOrWhiteSpace(existingDirectory.CreatedAt)
                        ? createdAt
                        : existingDirectory.CreatedAt;
                }
            }
            else if (directoryResponse.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
            {
                throw new HttpRequestException(
                    $"Não foi possível validar o vínculo Firebase do garçom ({(int)directoryResponse.StatusCode}): {body}",
                    null,
                    directoryResponse.StatusCode);
            }
        }

        var directory = new
        {
            waiterUid,
            storeUid = store.FirebaseUid,
            storeSlug,
            storeName = store.CustomerName,
            waiterNumber,
            employeeId = employee.Id.ToString(),
            active = employee.IsActive,
            createdAt = createdAt
        };
        var areas = areaIds.Distinct().ToDictionary(areaId => areaId.ToString(), _ => true);
        var access = new
        {
            active = employee.IsActive,
            displayName = employee.Name.Trim(),
            waiterNumber,
            role = "waiter",
            employeeId = employee.Id.ToString(),
            areas
        };
        var updates = new Dictionary<string, object>
        {
            [$"waiterDirectory/{waiterUid}"] = directory,
            [$"clients/{store.FirebaseUid}/restaurantAccess/{waiterUid}"] = access
        };

        using var content = JsonContent.Create(updates, options: JsonOptions);
        using var response = await Http.PatchAsync(
            $"{storeSession.DatabaseUrl}/.json?auth={Uri.EscapeDataString(storeSession.IdToken)}",
            content,
            cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Não foi possível salvar o acesso Firebase do garçom ({(int)response.StatusCode}): {responseBody}",
                null,
                response.StatusCode);

        return new WaiterAccountProvisioningResult(waiterUid, storeSession.RefreshToken);
    }

    public static string CreateWaiterStoreSlug(string customerNumber)
    {
        var normalized = SqliteStoreAccountRepository.NormalizeNumber(customerNumber);
        if (normalized.Length is < 3 or > 32 ||
            !char.IsAsciiLetterOrDigit(normalized[0]) ||
            !char.IsAsciiLetterOrDigit(normalized[^1]) ||
            normalized.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
            throw new ArgumentException(
                "O número da loja deve ter de 3 a 32 caracteres alfanuméricos ou hífens, começando e terminando com letra ou número.",
                nameof(customerNumber));
        return normalized.ToLowerInvariant();
    }

    public static string CreateWaiterLoginEmail(string storeSlug, string waiterNumber)
    {
        if (string.IsNullOrWhiteSpace(storeSlug) ||
            !System.Text.RegularExpressions.Regex.IsMatch(storeSlug, "^[a-z0-9][a-z0-9-]{1,38}[a-z0-9]$"))
            throw new ArgumentException("O código de acesso da loja é inválido.", nameof(storeSlug));
        var normalizedNumber = NormalizeWaiterNumber(waiterNumber);
        return $"garcom.{storeSlug}.{normalizedNumber}@login.sysmr.invalid";
    }

    private static string NormalizeMenuCategory(string category) =>
        string.IsNullOrWhiteSpace(category) ? "Sem categoria" : category.Trim();

    private static string CreateMenuCategoryId(string category)
    {
        var normalized = category.Trim().ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return new Guid(hash.AsSpan(0, 16)).ToString();
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

    private static async Task<AuthToken> CreateAuthUserAsync(
        string apiKey,
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        using var response = await Http.PostAsJsonAsync(
            $"https://identitytoolkit.googleapis.com/v1/accounts:signUp?key={Uri.EscapeDataString(apiKey)}",
            new { email, password, returnSecureToken = true },
            JsonOptions,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw FirebaseAccountRequestException.FromResponse(response.StatusCode, body);
        return JsonSerializer.Deserialize<AuthToken>(body, JsonOptions) is { LocalId.Length: > 0, IdToken.Length: > 0 } token
            ? token
            : throw new InvalidOperationException("O Firebase retornou uma conta de garçom inválida.");
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
        _ = CreateWaiterStoreSlug(number);
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

    private static string NormalizeWaiterNumber(string waiterNumber)
    {
        var normalized = waiterNumber.Trim().ToUpperInvariant();
        if (normalized.Length is < 1 or > 20 ||
            normalized.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
            throw new ArgumentException("O número do garçom deve ter de 1 a 20 caracteres: letras, números ou hífen.", nameof(waiterNumber));
        return normalized;
    }

    private sealed record FirebaseConfiguration(string ApiKey, string DatabaseUrl, string ProvisioningUrl);
    private sealed record AuthToken(string LocalId, string IdToken, string RefreshToken);
    private sealed record WaiterDirectoryProfile(
        string StoreUid,
        string StoreSlug,
        string WaiterNumber,
        string EmployeeId,
        string CreatedAt);
    private sealed record RefreshedToken(
        [property: JsonPropertyName("user_id")] string UserId,
        [property: JsonPropertyName("id_token")] string IdToken,
        [property: JsonPropertyName("refresh_token")] string RefreshToken);
    private sealed record ProvisionedAccount(string Uid);
    private sealed record StoreProfile(string CustomerNumber, string CustomerName, string? StoreType = null);
}

public sealed record FirebaseSession(string IdToken, string RefreshToken, string DatabaseUrl);
public sealed record WaiterAccountProvisioningResult(string FirebaseUid, string StoreRefreshToken);

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
