namespace SYSMR.Domain.Entities;

public enum PaymentMethod
{
    Cash,
    Pix,
    DebitCard,
    CreditCard,
    StoreCredit
}

public sealed record SaleItem(Guid ProductId, string ProductName, decimal Quantity, decimal UnitPrice)
{
    public decimal Total => decimal.Round(Quantity * UnitPrice, 2, MidpointRounding.AwayFromZero);
}

public sealed class Sale
{
    public Sale(
        Guid id,
        DateTimeOffset createdAt,
        PaymentMethod paymentMethod,
        IReadOnlyCollection<SaleItem> items,
        string? customerDocument = null,
        Guid? customerId = null,
        string sellerName = "Não informado")
    {
        if (items.Count == 0)
            throw new ArgumentException("A venda precisa conter ao menos um item.", nameof(items));
        if (items.Any(item => item.Quantity <= 0 || item.UnitPrice < 0))
            throw new ArgumentException("A venda contem itens invalidos.", nameof(items));
        if (paymentMethod == PaymentMethod.StoreCredit && customerId is null)
            throw new ArgumentException("Uma venda em conta precisa estar vinculada a um cliente cadastrado.", nameof(customerId));

        Id = id;
        CreatedAt = createdAt;
        PaymentMethod = paymentMethod;
        Items = items;
        CustomerDocument = customerDocument?.Trim();
        CustomerId = customerId;
        SellerName = string.IsNullOrWhiteSpace(sellerName)
            ? throw new ArgumentException("O vendedor da venda é obrigatório.", nameof(sellerName))
            : sellerName.Trim();
    }

    public Guid Id { get; }
    public DateTimeOffset CreatedAt { get; }
    public PaymentMethod PaymentMethod { get; }
    public IReadOnlyCollection<SaleItem> Items { get; }
    public string? CustomerDocument { get; }
    public Guid? CustomerId { get; }
    public string SellerName { get; }
    public decimal Total => Items.Sum(item => item.Total);
}
