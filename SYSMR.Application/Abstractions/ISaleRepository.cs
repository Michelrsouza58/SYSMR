using SYSMR.Domain.Entities;
using SYSMR.Application.Models;

namespace SYSMR.Application.Abstractions;

public interface ISaleRepository
{
    Task SaveAsync(Sale sale, CancellationToken cancellationToken = default);
    Task<decimal> GetSalesTotalAsync(CancellationToken cancellationToken = default);
    Task<int> GetSalesCountAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SaleHistoryEntry>> GetSalesAsync(string query, CancellationToken cancellationToken = default);
    Task<SaleRecord?> GetSaleAsync(Guid saleId, CancellationToken cancellationToken = default);
    Task ReturnSaleAsync(
        Guid saleId,
        IReadOnlyCollection<SaleItem> items,
        DateTimeOffset returnedAt,
        string actor,
        CancellationToken cancellationToken = default);
}
