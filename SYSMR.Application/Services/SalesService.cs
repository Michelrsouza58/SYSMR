using SYSMR.Application.Abstractions;
using SYSMR.Domain.Entities;
using SYSMR.Application.Models;

namespace SYSMR.Application.Services;

public sealed class SalesService(ISaleRepository sales)
{
    public async Task<Guid> CompleteAsync(
        IReadOnlyCollection<SaleItem> items,
        PaymentMethod paymentMethod,
        string? customerDocument,
        Guid? customerId = null,
        string sellerName = "Não informado",
        CancellationToken cancellationToken = default)
    {
        var sale = new Sale(Guid.NewGuid(), DateTimeOffset.UtcNow, paymentMethod, items, customerDocument, customerId, sellerName);
        await sales.SaveAsync(sale, cancellationToken);
        return sale.Id;
    }

    public Task<decimal> GetSalesTotalAsync(CancellationToken cancellationToken = default) =>
        sales.GetSalesTotalAsync(cancellationToken);

    public Task<int> GetSalesCountAsync(CancellationToken cancellationToken = default) =>
        sales.GetSalesCountAsync(cancellationToken);

    public Task<IReadOnlyList<SaleHistoryEntry>> GetSalesAsync(
        string query,
        CancellationToken cancellationToken = default,
        Guid? customerId = null) =>
        sales.GetSalesAsync(query, cancellationToken, customerId);

    public Task<SaleRecord?> GetSaleAsync(Guid saleId, CancellationToken cancellationToken = default) =>
        sales.GetSaleAsync(saleId, cancellationToken);

    public Task<IReadOnlyList<OpenAccountEntry>> GetOpenAccountsAsync(
        Guid customerId,
        CancellationToken cancellationToken = default) =>
        sales.GetOpenAccountsAsync(customerId, cancellationToken);

    public Task RegisterAccountPaymentAsync(
        Guid saleId,
        decimal amount,
        PaymentMethod paymentMethod,
        string actor,
        CancellationToken cancellationToken = default) =>
        sales.RegisterAccountPaymentAsync(saleId, amount, paymentMethod, DateTimeOffset.UtcNow, actor, cancellationToken);

    public Task ReturnSaleAsync(
        Guid saleId,
        IReadOnlyCollection<SaleItem> items,
        string actor = "Super Admin",
        CancellationToken cancellationToken = default) =>
        sales.ReturnSaleAsync(saleId, items, DateTimeOffset.UtcNow, actor, cancellationToken);
}
