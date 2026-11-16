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
        string sellerName = "Não informado",
        CreditTerms? creditTerms = null)
    {
        if (items.Count == 0)
            throw new ArgumentException("A venda precisa conter ao menos um item.", nameof(items));
        if (items.Any(item => item.Quantity <= 0 || item.UnitPrice < 0))
            throw new ArgumentException("A venda contem itens invalidos.", nameof(items));
        if (paymentMethod == PaymentMethod.StoreCredit && customerId is null)
            throw new ArgumentException("Uma venda em conta precisa estar vinculada a um cliente cadastrado.", nameof(customerId));
        if (creditTerms is not null && paymentMethod != PaymentMethod.CreditCard)
            throw new ArgumentException("Parcelamento só pode ser aplicado a cartão de crédito.", nameof(creditTerms));
        if (creditTerms is not null &&
            creditTerms.CalculateMinimumTotal(items.Sum(item => item.Total)) < creditTerms.Installments / 100m)
            throw new ArgumentException("O total no crédito deve permitir parcelas de pelo menos R$ 0,01.", nameof(creditTerms));

        Id = id;
        CreatedAt = createdAt;
        PaymentMethod = paymentMethod;
        CreditTerms = creditTerms;
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
    public CreditTerms? CreditTerms { get; }
    public decimal CashTotal => Items.Sum(item => item.Total);
    public decimal Total => CreditTerms?.CalculateTotal(CashTotal) ?? CashTotal;
}
