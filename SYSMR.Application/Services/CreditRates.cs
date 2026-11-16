namespace SYSMR.Application.Services;

public static class CreditRates
{
    public static IReadOnlyList<decimal> InfinitePayDefaults =>
        [3.15m, 5.39m, 6.12m, 6.85m, 7.57m, 8.28m, 8.99m, 9.80m, 10.38m, 11.06m, 11.74m, 12.40m];

    public static void Validate(IReadOnlyList<decimal> rates)
    {
        if (rates.Count != 12 || rates.Any(rate => rate < 0 || rate >= 100))
            throw new ArgumentException("Informe 12 taxas de 0% a menos de 100%.");
    }
}
