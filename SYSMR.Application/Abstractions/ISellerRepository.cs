using SYSMR.Application.Models;

namespace SYSMR.Application.Abstractions;

public interface ISellerRepository
{
    Task<IReadOnlyList<SellerRecord>> GetAllAsync(bool activeOnly = false, CancellationToken cancellationToken = default);
    Task SaveAsync(SellerRecord seller, CancellationToken cancellationToken = default);
}