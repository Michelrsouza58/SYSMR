namespace SYSMR.Application.Abstractions;

public enum CardPaymentStatus
{
    Approved,
    Declined,
    Cancelled,
    Error
}

public sealed record CardPaymentRequest(
    decimal Amount,
    string? Reference = null,
    int Installments = 1);

public sealed record CardPaymentResult(
    CardPaymentStatus Status,
    string? AuthorizationCode = null,
    string? CardBrand = null,
    string? Message = null);

/// <summary>
/// Ponto de integração com maquininhas de cartão (TEF/POS integrado).
/// A implementação atual é manual: o operador digita o valor na maquininha
/// e confirma o resultado no SYSMR. Integrações automatizadas (Stone,
/// PagSeguro, Cielo LIO, Getnet, TEF dedicado etc.) devem implementar
/// esta interface e ser registradas no lugar do provedor manual.
/// </summary>
public interface ICardPaymentProvider
{
    string Name { get; }

    /// <summary>True quando o provedor comunica-se diretamente com a maquininha.</summary>
    bool IsIntegrated { get; }

    Task<CardPaymentResult> ChargeAsync(CardPaymentRequest request, CancellationToken cancellationToken = default);
}
