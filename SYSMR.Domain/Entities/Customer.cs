namespace SYSMR.Domain.Entities;

public sealed class Customer
{
    public Customer(Guid id, string name, string document, string phone, string email, DateTimeOffset registeredAt, DateTimeOffset expiresAt,
        string postalCode = "", string street = "", string number = "", string complement = "", string neighborhood = "",
        string city = "", string state = "")
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
        PostalCode = postalCode.Trim();
        Street = street.Trim();
        Number = number.Trim();
        Complement = complement.Trim();
        Neighborhood = neighborhood.Trim();
        City = city.Trim();
        State = state.Trim();
    }

    public Guid Id { get; }
    public string Name { get; }
    public string Document { get; }
    public string Phone { get; }
    public string Email { get; }
    public DateTimeOffset RegisteredAt { get; }
    public DateTimeOffset ExpiresAt { get; }
    public string PostalCode { get; }
    public string Street { get; }
    public string Number { get; }
    public string Complement { get; }
    public string Neighborhood { get; }
    public string City { get; }
    public string State { get; }
    public bool NeedsReview => ExpiresAt <= DateTimeOffset.UtcNow;
    public string StatusLabel => NeedsReview ? "REVISAR" : "ATIVO";

    public Customer Renew(DateTimeOffset renewedAt) =>
        new(Id, Name, Document, Phone, Email, renewedAt, renewedAt.AddDays(30),
            PostalCode, Street, Number, Complement, Neighborhood, City, State);
}
