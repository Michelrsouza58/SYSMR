using SYSMR.Domain.Entities;

namespace SYSMR.Application.Models;

public sealed record RestaurantAreaRecord(Guid Id, string Name, int SortOrder, int TableCount, bool IsActive = true)
{
    public string StatusLabel => IsActive ? "Ativa" : "Inativa";
}
public sealed record RestaurantTableRecord(
    Guid Id,
    Guid AreaId,
    int Number,
    bool IsOccupied,
    decimal Total,
    bool IsActive = true,
    string CustomerName = "",
    string EmployeeName = "")
{
    public string StatusLabel => IsOccupied ? "OCUPADA" : "LIVRE";
    public string CustomerLabel => string.IsNullOrWhiteSpace(CustomerName) ? "Cliente: não informado" : $"Cliente: {CustomerName}";
    public string EmployeeLabel => string.IsNullOrWhiteSpace(EmployeeName) ? "Atendente: não informado" : $"Atendente: {EmployeeName}";
}
public sealed record RestaurantMenuOptionRecord(Guid Id, string Name, decimal ExtraPrice);
public sealed record RestaurantOrderSelection(Guid OptionId, decimal Quantity, decimal? ExtraPrice = null);
public sealed record WaiterCommandOption(string? OptionId, decimal Quantity, decimal? ExtraPrice = null);
public sealed record WaiterCommandItem(
    string? MenuItemId,
    decimal Quantity,
    string? Size = null,
    string? Observation = null,
    IReadOnlyList<WaiterCommandOption>? Options = null);
public sealed record RestaurantMenuItemRecord(
    Guid Id,
    string Name,
    string Category,
    decimal Price,
    bool IsActive,
    Guid? InventoryProductId = null,
    decimal InventoryQuantityPerUnit = 0m,
    IReadOnlyList<RestaurantMenuOptionRecord>? Options = null,
    string PrintTarget = "kitchen",
    decimal? PriceM = null,
    decimal? PriceG = null,
    string Description = "")
{
    public string StatusLabel => IsActive ? "Ativo" : "Inativo";
}
public sealed record RestaurantEmployeeRecord(
    Guid Id,
    string Name,
    string Role,
    bool IsActive,
    string? WaiterNumber = null,
    string? FirebaseUid = null,
    string? AreaIds = null,
    bool HasLocalPin = false)
{
    public string StatusLabel => IsActive ? "Ativo" : "Inativo";
    public bool HasWebAccess => !string.IsNullOrWhiteSpace(FirebaseUid);
    public bool HasLocalAccess => HasLocalPin;
}
public sealed record RestaurantOrderItemRecord(
    Guid Id,
    string Name,
    decimal Quantity,
    decimal UnitPrice,
    string? Observation = null,
    string PrintTarget = "kitchen",
    Guid? MenuItemId = null,
    string? VariantKey = null,
    decimal PaidQuantity = 0m)
{
    public decimal Total => Quantity * UnitPrice;
    public decimal RemainingQuantity => Quantity - PaidQuantity;
}
public sealed record RestaurantItemPaymentSelection(Guid ItemId, decimal Quantity);
public sealed record RestaurantOrderDisplayGroup(IReadOnlyList<RestaurantOrderItemRecord> Items)
{
    public string Name => Items[0].Name;
    public string? Observation => Items[0].Observation;
    public decimal Quantity => Items.Sum(item => item.Quantity);
    public decimal PaidQuantity => Items.Sum(item => item.PaidQuantity);
    public decimal UnitPrice => Items[0].UnitPrice;
    public decimal Total => Items.Sum(item => item.Total);

    public static IReadOnlyList<RestaurantOrderDisplayGroup> Create(IReadOnlyList<RestaurantOrderItemRecord> items)
    {
        var groups = new List<List<RestaurantOrderItemRecord>>();
        var index = new Dictionary<(Guid MenuId, string Variant, string Name, decimal Price), int>();
        foreach (var item in items)
        {
            if (item.MenuItemId is not Guid menuId || item.VariantKey is null ||
                !string.IsNullOrWhiteSpace(item.Observation))
            {
                groups.Add([item]);
                continue;
            }
            var key = (menuId, item.VariantKey, item.Name, item.UnitPrice);
            if (!index.TryGetValue(key, out var groupIndex))
            {
                groupIndex = groups.Count;
                index.Add(key, groupIndex);
                groups.Add([]);
            }
            groups[groupIndex].Add(item);
        }
        return groups.Select(group => new RestaurantOrderDisplayGroup(group)).ToArray();
    }
}
public sealed record KitchenPrintBatchRecord(
    Guid OrderId,
    int TableNumber,
    string AreaName,
    string EmployeeName,
    DateTimeOffset OpenedAt,
    IReadOnlyList<RestaurantOrderItemRecord> Items,
    int? DailyNumber = null,
    string? LastError = null,
    int? PrintBatchNumber = null,
    string CustomerName = "");
public sealed record RestaurantItemChangeNotice(
    string CommandId,
    Guid OrderId,
    int TableNumber,
    string AreaName,
    string ItemName,
    string PrintTarget,
    int? PrintBatchNumber,
    string Action,
    string OldObservation,
    string NewObservation,
    DateTimeOffset CreatedAt,
    string Status,
    string? Error);
public sealed record RestaurantOrderRecord(
    Guid Id,
    Guid TableId,
    string EmployeeName,
    DateTimeOffset OpenedAt,
    IReadOnlyList<RestaurantOrderItemRecord> Items,
    decimal PaidTotal = 0m,
    bool WaiterServiceChargeEnabled = false,
    int? DailyNumber = null,
    string CustomerName = "",
    bool HasUnallocatedPayments = false)
{
    public decimal Subtotal => decimal.Round(Items.Sum(item => item.Total), 2, MidpointRounding.AwayFromZero);
    public decimal WaiterServiceCharge =>
        WaiterServiceChargeEnabled
            ? decimal.Round(Subtotal * 0.10m, 2, MidpointRounding.AwayFromZero)
            : 0m;
    public decimal Total => Subtotal + WaiterServiceCharge;
    public decimal RemainingTotal => Math.Max(0, Total - PaidTotal);

    public decimal ItemPaymentAmount(IEnumerable<RestaurantItemPaymentSelection> selections)
    {
        var selected = selections.ToDictionary(selection => selection.ItemId, selection => selection.Quantity);
        var paidSubtotal = Items.Sum(item => item.PaidQuantity * item.UnitPrice);
        var selectedSubtotal = Items.Sum(item => selected.GetValueOrDefault(item.Id) * item.UnitPrice);
        var newSubtotal = decimal.Round(paidSubtotal + selectedSubtotal, 2, MidpointRounding.AwayFromZero);
        var charge = WaiterServiceChargeEnabled
            ? decimal.Round(newSubtotal * 0.10m, 2, MidpointRounding.AwayFromZero) : 0m;
        return newSubtotal + charge - PaidTotal;
    }
}
public sealed record WaiterCommandRecord(
    string? Id,
    string? Type,
    string? AreaId,
    string? TableId,
    string? EmployeeUid,
    string? MenuItemId,
    decimal Quantity,
    string? Status,
    string? CreatedAt,
    string? Observation = null,
    string? OrderItemId = null,
    IReadOnlyList<WaiterCommandOption>? Options = null,
    IReadOnlyList<WaiterCommandItem>? Items = null,
    string? CustomerName = null);
public sealed record WaiterCommandResult(string Status, string? Message, Guid? OrderId);
public sealed record RestaurantCashSessionRecord(
    Guid Id,
    DateTimeOffset OpenedAt,
    decimal OpeningAmount,
    decimal CashSales,
    decimal OtherSales,
    decimal CashExpenses = 0m);
public sealed record RestaurantExpenseRecord(
    Guid Id,
    DateTimeOffset CreatedAt,
    string Category,
    string Description,
    decimal Amount,
    bool PaidFromCash);
public sealed record RestaurantDashboardCategory(string Category, decimal Total);
public sealed record RestaurantWaiterSales(string Name, int Orders, decimal Total);
public sealed record RestaurantDashboardDay(DateOnly Date, decimal Total, int Payments);
public sealed record RestaurantDashboardMethod(PaymentMethod PaymentMethod, decimal Total, int Payments);
public sealed record RestaurantDashboard(
    DateOnly From,
    DateOnly To,
    decimal TotalRevenue,
    int OrderCount,
    decimal AverageTicket,
    IReadOnlyList<RestaurantDashboardDay> Days,
    IReadOnlyList<RestaurantDashboardMethod> Methods,
    decimal TotalExpenses = 0m,
    IReadOnlyList<RestaurantDashboardCategory>? ExpenseCategories = null);
public sealed record RestaurantPaymentRecord(
    Guid OrderId,
    PaymentMethod PaymentMethod,
    decimal Amount,
    DateTimeOffset PaidAt,
    decimal RemainingTotal = 0m);
