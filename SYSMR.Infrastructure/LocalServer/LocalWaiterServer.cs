using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SYSMR.Application.Models;
using SYSMR.Infrastructure;
using SYSMR.Infrastructure.Persistence;

namespace SYSMR.Infrastructure.LocalServer;

/// <summary>
/// Servidor HTTP local (rede Wi-Fi da loja) que permite aos garçons enviarem
/// comandas pelo celular mesmo sem internet. Roda embutido no SYSMR e grava
/// no mesmo SQLite local, reutilizando o fluxo de comandos do garçom.
/// </summary>
public sealed class LocalWaiterServer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SqliteRestaurantRepository _restaurant;
    private readonly string _storeName;
    private readonly ConcurrentDictionary<string, WaiterSession> _sessions = new(StringComparer.Ordinal);

    private WebApplication? _app;
    private Task? _runTask;

    public LocalWaiterServer(SqliteRestaurantRepository restaurant, string storeName, int port = 8080)
    {
        _restaurant = restaurant;
        _storeName = string.IsNullOrWhiteSpace(storeName) ? "SYSMR" : storeName.Trim();
        Port = port;
    }

    public int Port { get; }
    public bool IsRunning => _app is not null;

    public static IReadOnlyList<string> GetLocalUrls(int port)
    {
        var urls = new List<string> { $"http://localhost:{port}" };
        try
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                    networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback)
                    continue;
                foreach (var address in networkInterface.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(address.Address))
                        urls.Add($"http://{address.Address}:{port}");
                }
            }
        }
        catch (NetworkInformationException)
        {
            // Sem acesso às interfaces de rede: mantém apenas localhost.
        }
        return urls;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_app is not null)
            return;

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls($"http://0.0.0.0:{Port}");

        var app = builder.Build();
        ConfigureEndpoints(app);
        try
        {
            await app.StartAsync(cancellationToken);
        }
        catch
        {
            await app.DisposeAsync();
            throw new InvalidOperationException(
                $"Não foi possível iniciar o servidor local na porta {Port}. Verifique se outra aplicação já usa essa porta.");
        }
        _app = app;
        _runTask = app.WaitForShutdownAsync(cancellationToken);
    }

    public async Task StopAsync()
    {
        var app = _app;
        _app = null;
        if (app is null)
            return;
        try
        {
            await app.StopAsync(TimeSpan.FromSeconds(5));
            if (_runTask is not null)
                await _runTask;
        }
        catch (OperationCanceledException)
        {
            // Encerramento normal do host.
        }
        finally
        {
            _runTask = null;
            await app.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private void ConfigureEndpoints(WebApplication app)
    {
        var webappDirectory = FindWebappDirectory();
        if (webappDirectory is null)
            app.MapGet("/", () => Results.Content(WaiterAppPage.Html, "text/html; charset=utf-8"));

        app.MapGet("/api/status", () => Results.Json(new
        {
            app = "SYSMR",
            store = _storeName,
            mode = "local",
            webapp = webappDirectory is not null ? "react" : "embedded"
        }, JsonOptions));

        app.MapPost("/api/login", async (HttpContext http) =>
        {
            try
            {
                var request = await http.Request.ReadFromJsonAsync<LoginRequest>(JsonOptions);
                if (request is null)
                    return Results.BadRequest(new { error = "Requisição inválida." });

                var employee = await _restaurant.VerifyWaiterLocalPinAsync(
                    request.WaiterNumber ?? string.Empty,
                    request.Pin ?? string.Empty,
                    http.RequestAborted);
                if (employee is null)
                    return Results.Json(
                        new { error = "Número ou PIN inválidos, ou funcionário sem acesso local." },
                        statusCode: StatusCodes.Status401Unauthorized);

                var token = Guid.NewGuid().ToString("N");
                _sessions[token] = new WaiterSession(employee, DateTimeOffset.UtcNow.AddHours(12));
                return Results.Json(new
                {
                    token,
                    name = employee.Name,
                    role = employee.Role
                }, JsonOptions);
            }
            catch (Exception exception)
            {
                DebugLog.Write($"[LocalWaiterServer] ERRO no login: {exception}");
                return Results.Json(
                    new { error = $"Erro interno: {exception.Message}" },
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        app.MapGet("/api/areas", async (HttpContext http) =>
        {
            var session = await AuthenticateAsync(http);
            if (session is null)
                return Unauthorized();

            var authorizedAreaIds = ParseAreaIds(session.Employee.AreaIds);
            var areas = new List<object>();
            foreach (var area in await _restaurant.GetAreasAsync(http.RequestAborted))
            {
                if (!area.IsActive || !authorizedAreaIds.Contains(area.Id))
                    continue;
                var tableRecords = await _restaurant.GetTablesAsync(area.Id, http.RequestAborted);
                var tables = new List<object>();
                foreach (var table in tableRecords)
                {
                    object? openOrder = null;
                    if (table.IsOccupied)
                    {
                        var order = await _restaurant.GetOpenOrderAsync(table.Id, http.RequestAborted);
                        if (order is not null)
                            openOrder = new
                            {
                                id = order.Id,
                                subtotal = order.Subtotal,
                                items = order.Items.Select(item => new
                                {
                                    id = item.Id,
                                    name = item.Name,
                                    quantity = item.Quantity,
                                    unitPrice = item.UnitPrice,
                                    note = string.IsNullOrWhiteSpace(item.Observation) ? null : item.Observation
                                }).ToArray()
                            };
                    }
                    tables.Add(new
                    {
                        id = table.Id,
                        number = table.Number,
                        isOccupied = table.IsOccupied,
                        total = table.Total,
                        openOrder
                    });
                }
                areas.Add(new { id = area.Id, name = area.Name, tables });
            }
            return Results.Json(areas, JsonOptions);
        });

        app.MapGet("/api/menu", async (HttpContext http) =>
        {
            var session = await AuthenticateAsync(http);
            if (session is null)
                return Unauthorized();

            var items = (await _restaurant.GetMenuItemsAsync(activeOnly: true, http.RequestAborted))
                .Select(item => new
                {
                    id = item.Id,
                    name = item.Name,
                    description = item.Description,
                    category = item.Category,
                    price = item.Price,
                    options = (item.Options ?? Array.Empty<RestaurantMenuOptionRecord>())
                        .Select(option => new
                        {
                            id = option.Id,
                            name = option.Name,
                            extraPrice = option.ExtraPrice
                        })
                        .ToArray()
                })
                .ToArray();
            return Results.Json(items, JsonOptions);
        });

        app.MapGet("/api/tables/{tableId:guid}/order", async (HttpContext http, Guid tableId) =>
        {
            var session = await AuthenticateAsync(http);
            if (session is null)
                return Unauthorized();
            if (!await IsTableAuthorizedAsync(tableId, session.Employee, http.RequestAborted))
                return Results.Json(new { error = "Mesa fora das áreas autorizadas para este garçom." },
                    statusCode: StatusCodes.Status403Forbidden);

            var order = await _restaurant.GetOpenOrderAsync(tableId, http.RequestAborted);
            if (order is null)
                return Results.Json(null, JsonOptions);

            return Results.Json(new
            {
                id = order.Id,
                employeeName = order.EmployeeName,
                openedAt = order.OpenedAt,
                dailyNumber = order.DailyNumber,
                items = order.Items.Select(item => new
                {
                    id = item.Id,
                    name = item.Name,
                    quantity = item.Quantity,
                    unitPrice = item.UnitPrice,
                    observation = item.Observation,
                    total = item.Total
                }).ToArray(),
                subtotal = order.Subtotal,
                serviceCharge = order.WaiterServiceCharge,
                total = order.Total
            }, JsonOptions);
        });

        app.MapPost("/api/commands", async (HttpContext http) =>
        {
            var session = await AuthenticateAsync(http);
            if (session is null)
                return Unauthorized();

            var request = await http.Request.ReadFromJsonAsync<CommandRequest>(JsonOptions);
            if (request is null || string.IsNullOrWhiteSpace(request.Type))
                return Results.BadRequest(new { error = "Comando inválido." });

            if (request.Type.Trim().Equals("removeOne", StringComparison.OrdinalIgnoreCase) ||
                request.Type.Trim().Equals("updateOneObservation", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "As ações unitárias ainda não estão disponíveis no modo local." });

            // Normaliza o tipo de comando (converte 'addItem' ou 'additem' para 'add_item')
            var normalizedType = request.Type.Trim().ToLowerInvariant() switch
            {
                "additem" or "add_item" => "add_item",
                "removeitem" or "remove_item" => "remove_item",
                _ => request.Type.Trim()
            };

            var command = new WaiterCommandRecord(
                Id: string.IsNullOrWhiteSpace(request.Id) ? Guid.NewGuid().ToString() : request.Id,
                Type: normalizedType,
                AreaId: request.AreaId,
                TableId: request.TableId,
                EmployeeUid: SqliteRestaurantRepository.CreateLocalWaiterUid(session.Employee.Id),
                MenuItemId: request.MenuItemId,
                Quantity: request.Quantity,
                Status: "pending",
                CreatedAt: DateTimeOffset.UtcNow.ToString("O"),
                Observation: request.Observation,
                OrderItemId: request.OrderItemId,
                Options: request.Options);

            var result = await _restaurant.ProcessWaiterCommandAsync(command, http.RequestAborted);
            DebugLog.Write($"[LocalWaiterServer] Comando {command.Type} processado: {result.Status}");
            if (string.Equals(result.Status, "applied", StringComparison.OrdinalIgnoreCase))
            {
                DebugLog.Write("[LocalWaiterServer] Notificando UI...");
                RestaurantOrderNotifier.NotifyOrderChanged();
            }

            return Results.Json(new
            {
                status = result.Status,
                message = result.Message,
                orderId = result.OrderId
            }, JsonOptions);
        });

        if (webappDirectory is not null)
            MapWebappFiles(app, webappDirectory);
    }

    private static string? FindWebappDirectory()
    {
        var candidates = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SYSMR", "waiter-app"),
            Path.Combine(AppContext.BaseDirectory, "waiter-app")
        };
        return candidates.FirstOrDefault(candidate =>
            Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "index.html")));
    }

    private static readonly IReadOnlyDictionary<string, string> ContentTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".html"] = "text/html; charset=utf-8",
            [".js"] = "text/javascript; charset=utf-8",
            [".css"] = "text/css; charset=utf-8",
            [".json"] = "application/json; charset=utf-8",
            [".svg"] = "image/svg+xml",
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".ico"] = "image/x-icon",
            [".woff"] = "font/woff",
            [".woff2"] = "font/woff2",
            [".webmanifest"] = "application/manifest+json",
            [".map"] = "application/json"
        };

    private static void MapWebappFiles(WebApplication app, string webappDirectory)
    {
        var root = Path.GetFullPath(webappDirectory);
        app.MapGet("/{**path}", (HttpContext http, string? path) =>
        {
            var relative = string.IsNullOrWhiteSpace(path) ? "index.html" : path;
            var fullPath = Path.GetFullPath(Path.Combine(root, relative));
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            {
                fullPath = Path.Combine(root, "index.html");
                if (!File.Exists(fullPath))
                    return Results.NotFound();
            }

            var contentType = ContentTypes.TryGetValue(Path.GetExtension(fullPath), out var known)
                ? known
                : "application/octet-stream";
            return Results.File(fullPath, contentType);
        });
    }

    private async Task<WaiterSession?> AuthenticateAsync(HttpContext http)
    {
        var token = http.Request.Headers["X-Waiter-Token"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(token) ||
            !_sessions.TryGetValue(token, out var session))
            return null;
        if (session.ExpiresAt < DateTimeOffset.UtcNow)
        {
            _sessions.TryRemove(token, out _);
            return null;
        }

        var renewed = session with { ExpiresAt = DateTimeOffset.UtcNow.AddHours(12) };
        _sessions[token] = renewed;

        var current = (await _restaurant.GetEmployeesAsync(http.RequestAborted))
            .SingleOrDefault(employee => employee.Id == renewed.Employee.Id);
        return current is { IsActive: true } ? renewed with { Employee = current } : null;
    }

    private async Task<bool> IsTableAuthorizedAsync(
        Guid tableId,
        RestaurantEmployeeRecord employee,
        CancellationToken cancellationToken)
    {
        var authorizedAreaIds = ParseAreaIds(employee.AreaIds);
        foreach (var areaId in authorizedAreaIds)
        {
            var tables = await _restaurant.GetTablesAsync(areaId, cancellationToken);
            if (tables.Any(table => table.Id == tableId))
                return true;
        }
        return false;
    }

    private static HashSet<Guid> ParseAreaIds(string? areaIds) =>
        (areaIds ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => Guid.TryParse(value, out var id) ? id : Guid.Empty)
            .ToHashSet();

    private static IResult Unauthorized() =>
        Results.Json(new { error = "Sessão expirada. Entre novamente." },
            statusCode: StatusCodes.Status401Unauthorized);

    private sealed record WaiterSession(RestaurantEmployeeRecord Employee, DateTimeOffset ExpiresAt);

    private sealed class LoginRequest
    {
        public string? WaiterNumber { get; set; }
        public string? Pin { get; set; }
    }

    private sealed class CommandRequest
    {
        public string? Id { get; set; }
        public string? Type { get; set; }
        public string? AreaId { get; set; }
        public string? TableId { get; set; }
        public string? MenuItemId { get; set; }
        public decimal Quantity { get; set; }
        public string? Observation { get; set; }
        public string? OrderItemId { get; set; }
        public WaiterCommandOption[]? Options { get; set; }
    }
}