using SYSMR.Domain.Entities;

namespace SYSMR.Application.Models;

public sealed record SaleHistoryEntry(
    Guid Id,
    DateTimeOffset CreatedAt,
    PaymentMethod PaymentMethod,
    decimal Total,
    string Customer,
    string SellerName,
    bool IsReturned)
{
    public bool HasAnyReturn { get; init; }
    public Guid? CustomerId { get; init; }
    public decimal AmountPaid { get; init; }
    public decimal AmountReturned { get; init; }
    public decimal BalanceDue { get; init; }

    public string PaymentLabel => PaymentMethod switch
    {
        PaymentMethod.Cash => "Dinheiro",
        PaymentMethod.Pix => "Pix",
        PaymentMethod.DebitCard => "Cartão de débito",
        PaymentMethod.CreditCard => "Cartão de crédito",
        PaymentMethod.StoreCredit => "Conta / notinha",
        _ => PaymentMethod.ToString()
    };
}

public sealed record SaleRecord(
    SaleHistoryEntry Summary,
    IReadOnlyList<SaleItem> Items,
    IReadOnlyDictionary<Guid, decimal> ReturnedQuantities)
{
    public IReadOnlyList<SalePaymentEntry> Payments { get; init; } = [];
}

public sealed record SalePaymentEntry(
    DateTimeOffset PaidAt,
    decimal Amount,
    PaymentMethod PaymentMethod);

public sealed record OpenAccountEntry(
    Guid SaleId,
    Guid CustomerId,
    string Customer,
    DateTimeOffset CreatedAt,
    decimal Total,
    decimal AmountPaid,
    decimal AmountReturned,
    decimal BalanceDue);
