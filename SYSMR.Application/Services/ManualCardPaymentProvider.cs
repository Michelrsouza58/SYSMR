using SYSMR.Application.Abstractions;

namespace SYSMR.Application.Services;

/// <summary>
/// Provedor padrão: sem comunicação com a maquininha. O operador realiza a
/// cobrança no terminal e apenas confirma no SYSMR. Substitua por um provedor
/// integrado (TEF/POS) quando a integração estiver disponível.
/// </summary>
public sealed class ManualCardPaymentProvider : ICardPaymentProvider
{
    public string Name => "Maquininha manual (confirmação do operador)";

    public bool IsIntegrated => false;

    public Task<CardPaymentResult> ChargeAsync(
        CardPaymentRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new CardPaymentResult(
            CardPaymentStatus.Approved,
            Message: "Confirme a cobrança digitada manualmente na maquininha."));
}
