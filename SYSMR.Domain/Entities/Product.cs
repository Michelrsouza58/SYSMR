namespace SYSMR.Domain.Entities;

public sealed class Product
{
    public Product(
        Guid id,
        string name,
        string barcode,
        string scaleCode,
        decimal costPrice,
        decimal salePrice,
        decimal stock,
        decimal minimumStock,
        string category,
        string ncm,
        string cest,
        bool soldByWeight,
        string unitOfMeasure = "UN")
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("O nome do produto e obrigatorio.", nameof(name));
        if (costPrice < 0 || salePrice < 0 || stock < 0 || minimumStock < 0)
            throw new ArgumentOutOfRangeException(nameof(salePrice), "Valores e estoque nao podem ser negativos.");

        Id = id;
        Name = name.Trim();
        Barcode = barcode.Trim();
        ScaleCode = scaleCode.Trim();
        CostPrice = costPrice;
        SalePrice = salePrice;
        Stock = stock;
        MinimumStock = minimumStock;
        Category = category.Trim();
        Ncm = ncm.Trim();
        Cest = cest.Trim();
        SoldByWeight = soldByWeight;
        UnitOfMeasure = string.IsNullOrWhiteSpace(unitOfMeasure)
            ? throw new ArgumentException("A unidade de medida é obrigatória.", nameof(unitOfMeasure))
            : unitOfMeasure.Trim().ToUpperInvariant();
    }

    public Guid Id { get; }
    public string Name { get; }
    public string Barcode { get; }
    public string ScaleCode { get; }
    public decimal CostPrice { get; }
    public decimal SalePrice { get; }
    public decimal Stock { get; }
    public decimal MinimumStock { get; }
    public string Category { get; }
    public string Ncm { get; }
    public string Cest { get; }
    public bool SoldByWeight { get; }
    public string UnitOfMeasure { get; }
}
