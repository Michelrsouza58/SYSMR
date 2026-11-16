using System.Globalization;
using SYSMR.Domain.Entities;

namespace SYSMR.UI;

internal static class InstallmentDisplay
{
    public static string Format(CreditTerms terms, decimal cashTotal, decimal creditTotal, IFormatProvider? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        return $"Valor à vista: {cashTotal.ToString("C2", culture)}\n" +
               $"Crédito {terms.Installments}x: {creditTotal.ToString("C2", culture)}";
    }
}
