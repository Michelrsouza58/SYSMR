using SYSMR.Application.Abstractions;
using SYSMR.Application.Models;

namespace SYSMR.Application.Services;

public sealed class ProductCatalogService(IProductRepository products)
{
    public Task<IReadOnlyList<SYSMR.Domain.Entities.Product>> SearchAsync(string query, CancellationToken cancellationToken = default) =>
        products.SearchAsync(query, cancellationToken);

    public async Task<ScannedProduct?> FindScannedProductAsync(string barcode, CancellationToken cancellationToken = default)
    {
        var normalized = barcode.Trim();
        if (normalized.Length == 0)
            return null;

        var exactMatch = await products.FindByBarcodeAsync(normalized, cancellationToken);
        if (exactMatch is not null)
            return new ScannedProduct(exactMatch, 1m);

        // SYSMR scale-label format: 20 + 5-digit product code + 5-digit weight in grams + EAN-13 check digit.
        if (normalized.Length == 13 && normalized.StartsWith("20", StringComparison.Ordinal) &&
            normalized.All(char.IsDigit) && HasValidEan13CheckDigit(normalized))
        {
            var scaleCode = normalized.Substring(2, 5);
            if (!int.TryParse(normalized.Substring(7, 5), out var grams) || grams <= 0)
                return null;

            var product = await products.FindByScaleCodeAsync(scaleCode, cancellationToken);
            if (product is not null && product.SoldByWeight)
                return new ScannedProduct(product, grams / 1000m);
        }

        return null;
    }

    private static bool HasValidEan13CheckDigit(string barcode)
    {
        var sum = 0;
        for (var index = 0; index < 12; index++)
            sum += (barcode[index] - '0') * (index % 2 == 0 ? 1 : 3);
        return (10 - sum % 10) % 10 == barcode[12] - '0';
    }
}
