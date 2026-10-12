using SYSMR.Domain.Entities;

namespace SYSMR.Application.Abstractions;

public interface IProductRepository
{
    Task<Product?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Product?> FindByBarcodeAsync(string barcode, CancellationToken cancellationToken = default);
    Task<Product?> FindByScaleCodeAsync(string scaleCode, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Product>> SearchAsync(string query, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Product>> SearchLowStockAsync(decimal maximumQuantity, CancellationToken cancellationToken = default);
    Task SaveAsync(Product product, CancellationToken cancellationToken = default);
}
