namespace SYSMR.Domain.Entities;

public sealed class Customer
{
    public Customer(Guid id, string name, string document, string phone, string email, DateTimeOffset registeredAt, DateTimeOffset expiresAt)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("O nome do cliente é obrigatório.", nameof(name));
        if (expiresAt < registeredAt)
            throw new ArgumentOutOfRangeException(nameof(expiresAt));

        Id = id;
        Name = name.Trim();
        Document = document.Trim();
        Phone = phone.Trim();
        Email = email.Trim();
        RegisteredAt = registeredAt;
        ExpiresAt = expiresAt;
    }

    public Guid Id { get; }
    public string Name { get; }
    public string Document { get; }
    public string Phone { get; }
    public string Email { get; }
    public DateTimeOffset RegisteredAt { get; }
    public DateTimeOffset ExpiresAt { get; }
    public bool NeedsReview => ExpiresAt <= DateTimeOffset.UtcNow;
    public string StatusLabel => NeedsReview ? "REVISAR" : "ATIVO";

    public Customer Renew(DateTimeOffset renewedAt) =>
        new(Id, Name, Document, Phone, Email, renewedAt, renewedAt.AddDays(30));
}
