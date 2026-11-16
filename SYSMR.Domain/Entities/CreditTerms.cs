namespace SYSMR.Domain.Entities;

public sealed record CreditTerms(int Installments, decimal FeePercent)
{
    public decimal CalculateTotal(decimal cashPrice)
    {
        var minimumTotal = CalculateMinimumTotal(cashPrice);
        return decimal.Ceiling(minimumTotal * 100m / Installments) * Installments / 100m;
    }

    public decimal CalculateMinimumTotal(decimal cashPrice)
    {
        if (Installments is < 1 or > 12 || FeePercent is < 0 or >= 100 || cashPrice < 0)
            throw new ArgumentOutOfRangeException(nameof(FeePercent), "Informe parcelas de 1 a 12 e taxa de 0% a menos de 100%.");
        return decimal.Ceiling(cashPrice / (1m - FeePercent / 100m) * 100m) / 100m;
    }

    public IReadOnlyList<decimal> Split(decimal total)
    {
        if (Installments is < 1 or > 12)
            throw new ArgumentOutOfRangeException(nameof(Installments), "Informe de 1 a 12 parcelas.");
        if (total < 0 || decimal.Round(total, 2) != total)
            throw new ArgumentOutOfRangeException(nameof(total), "Informe um total não negativo em centavos.");
        var cents = decimal.ToInt64(total * 100m);
        var each = cents / Installments;
        var remainder = cents % Installments;
        return Enumerable.Range(0, Installments).Select(i => (each + (i < remainder ? 1 : 0)) / 100m).ToArray();
    }
}
