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

    public string PaymentLabel => PaymentMethod switch
    {
        PaymentMethod.Cash => "Dinheiro",
        PaymentMethod.Pix => "Pix",
        PaymentMethod.DebitCard => "Cartão de débito",
        PaymentMethod.CreditCard => "Cartão de crédito",
        _ => PaymentMethod.ToString()
    };
}

public sealed record SaleRecord(
    SaleHistoryEntry Summary,
    IReadOnlyList<SaleItem> Items,
    IReadOnlyDictionary<Guid, decimal> ReturnedQuantities);
