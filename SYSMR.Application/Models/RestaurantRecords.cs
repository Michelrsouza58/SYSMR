using SYSMR.Domain.Entities;

namespace SYSMR.Application.Models;

public sealed record RestaurantAreaRecord(Guid Id, string Name, int SortOrder, int TableCount);
public sealed record RestaurantTableRecord(Guid Id, Guid AreaId, int Number, bool IsOccupied, decimal Total)
{
    public string StatusLabel => IsOccupied ? "OCUPADA" : "LIVRE";
}
public sealed record RestaurantMenuItemRecord(
    Guid Id,
    string Name,
    string Category,
    decimal Price,
    bool IsActive,
    Guid? InventoryProductId = null,
    decimal InventoryQuantityPerUnit = 0m)
{
    public string StatusLabel => IsActive ? "Ativo" : "Inativo";
}
public sealed record RestaurantEmployeeRecord(Guid Id, string Name, string Role, bool IsActive)
{
    public string StatusLabel => IsActive ? "Ativo" : "Inativo";
}
public sealed record RestaurantOrderItemRecord(Guid Id, string Name, decimal Quantity, decimal UnitPrice)
{
    public decimal Total => Quantity * UnitPrice;
}
public sealed record RestaurantOrderRecord(
    Guid Id,
    Guid TableId,
    string EmployeeName,
    DateTimeOffset OpenedAt,
    IReadOnlyList<RestaurantOrderItemRecord> Items,
    decimal PaidTotal = 0m,
    bool WaiterServiceChargeEnabled = false)
{
    public decimal Subtotal => Items.Sum(item => item.Total);
    public decimal WaiterServiceCharge =>
        WaiterServiceChargeEnabled
            ? decimal.Round(Subtotal * 0.10m, 2, MidpointRounding.AwayFromZero)
            : 0m;
    public decimal Total => Subtotal + WaiterServiceCharge;
    public decimal RemainingTotal => Math.Max(0, Total - PaidTotal);
}
public sealed record RestaurantCashSessionRecord(
    Guid Id,
    DateTimeOffset OpenedAt,
    decimal OpeningAmount,
    decimal CashSales,
    decimal OtherSales);
public sealed record RestaurantPaymentRecord(
    Guid OrderId,
    PaymentMethod PaymentMethod,
    decimal Amount,
    DateTimeOffset PaidAt,
    decimal RemainingTotal = 0m);
