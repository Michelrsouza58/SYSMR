using System.Globalization;
using System.Text;

namespace SYSMR.Application.Services;

public static class PortugueseSpeechParser
{
    private static readonly IReadOnlyDictionary<string, int> NumberWords = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["zero"] = 0, ["um"] = 1, ["uma"] = 1, ["dois"] = 2, ["duas"] = 2,
        ["tres"] = 3, ["quatro"] = 4, ["cinco"] = 5, ["seis"] = 6, ["meia"] = 6,
        ["sete"] = 7, ["oito"] = 8, ["nove"] = 9, ["dez"] = 10, ["onze"] = 11,
        ["doze"] = 12, ["treze"] = 13, ["quatorze"] = 14, ["catorze"] = 14,
        ["quinze"] = 15, ["dezesseis"] = 16, ["dezessete"] = 17, ["dezoito"] = 18,
        ["dezenove"] = 19, ["vinte"] = 20, ["trinta"] = 30, ["quarenta"] = 40,
        ["cinquenta"] = 50, ["sessenta"] = 60, ["setenta"] = 70, ["oitenta"] = 80,
        ["noventa"] = 90, ["cem"] = 100, ["cento"] = 100, ["duzentos"] = 200,
        ["trezentos"] = 300, ["quatrocentos"] = 400, ["quinhentos"] = 500,
        ["seiscentos"] = 600, ["setecentos"] = 700, ["oitocentos"] = 800,
        ["novecentos"] = 900
    };

    private static readonly IReadOnlyDictionary<string, char> DigitWords = new Dictionary<string, char>(StringComparer.Ordinal)
    {
        ["zero"] = '0', ["um"] = '1', ["uma"] = '1', ["dois"] = '2', ["duas"] = '2',
        ["tres"] = '3', ["quatro"] = '4', ["cinco"] = '5', ["seis"] = '6', ["meia"] = '6',
        ["sete"] = '7', ["oito"] = '8', ["nove"] = '9'
    };

    public static bool TryParseNumber(string spokenText, out decimal value)
    {
        value = 0;
        var normalized = Normalize(spokenText).Replace("r$", string.Empty, StringComparison.Ordinal).Trim();
        if (decimal.TryParse(normalized, NumberStyles.Number | NumberStyles.AllowCurrencySymbol, CultureInfo.GetCultureInfo("pt-BR"), out value) ||
            decimal.TryParse(normalized, NumberStyles.Number | NumberStyles.AllowCurrencySymbol, CultureInfo.InvariantCulture, out value))
            return true;

        var realMarkerLength = 6;
        var realIndex = normalized.IndexOf(" reais", StringComparison.Ordinal);
        if (realIndex < 0)
        {
            realMarkerLength = 5;
            realIndex = normalized.IndexOf(" real", StringComparison.Ordinal);
        }
        if (realIndex >= 0)
        {
            var wholeText = normalized[..realIndex];
            var remainder = normalized[(realIndex + realMarkerLength)..].Trim();
            if (TryParseIntegerPhrase(wholeText, out var whole))
            {
                var centsIndex = remainder.IndexOf("centavo", StringComparison.Ordinal);
                var centsText = centsIndex >= 0 ? remainder[..centsIndex] : remainder;
                var conjunction = centsText.LastIndexOf(" e ", StringComparison.Ordinal);
                if (conjunction >= 0)
                    centsText = centsText[(conjunction + 3)..];
                if (TryParseIntegerPhrase(centsText, out var cents) && cents is >= 0 and < 100)
                {
                    value = whole + cents / 100m;
                    return true;
                }
                if (centsText.Length == 0)
                {
                    value = whole;
                    return true;
                }
            }
        }

        var separatorIndex = FindDecimalSeparator(normalized);
        if (separatorIndex >= 0)
        {
            var separatorLength = normalized.AsSpan(separatorIndex).StartsWith("virgula", StringComparison.Ordinal) ? 7 : 5;
            var left = normalized[..separatorIndex];
            var right = normalized[(separatorIndex + separatorLength)..];
            if (TryParseIntegerPhrase(left, out var whole) &&
                TryParseIntegerPhrase(right, out var fraction) &&
                fraction >= 0)
            {
                var scale = FractionScale(right, fraction);
                value = whole + fraction / scale;
                return true;
            }
        }

        return TryParseIntegerPhrase(normalized, out value);
    }

    public static bool TryParseDigits(string spokenText, out string digits)
    {
        var normalized = Normalize(spokenText);
        var output = new StringBuilder();
        foreach (var token in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.All(char.IsDigit))
                output.Append(token);
            else if (DigitWords.TryGetValue(token, out var digit))
                output.Append(digit);
            else
            {
                digits = string.Empty;
                return false;
            }
        }

        digits = output.ToString();
        return digits.Length > 0;
    }

    private static int FindDecimalSeparator(string text)
    {
        var wordSeparator = text.IndexOf("virgula", StringComparison.Ordinal);
        if (wordSeparator >= 0)
            return wordSeparator;
        wordSeparator = text.IndexOf("ponto", StringComparison.Ordinal);
        if (wordSeparator >= 0)
            return wordSeparator;
        return -1;
    }

    private static decimal FractionScale(string spokenFraction, decimal fraction)
    {
        var digits = spokenFraction.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (decimal.Truncate(fraction) == fraction)
            digits = Math.Max(digits, fraction == 0 ? 1 : fraction.ToString("0", CultureInfo.InvariantCulture).Length);
        return (decimal)Math.Pow(10, Math.Clamp(digits, 1, 6));
    }

    private static bool TryParseIntegerPhrase(string text, out decimal value)
    {
        value = 0;
        var cleaned = text.Replace("reais", string.Empty, StringComparison.Ordinal)
            .Replace("real", string.Empty, StringComparison.Ordinal)
            .Replace("centavos", string.Empty, StringComparison.Ordinal)
            .Replace("centavo", string.Empty, StringComparison.Ordinal)
            .Trim();
        if (decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out value) ||
            decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.GetCultureInfo("pt-BR"), out value))
            return true;

        decimal current = 0;
        decimal thousands = 0;
        var foundNumber = false;
        foreach (var token in cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token == "e")
                continue;
            if (token == "mil")
            {
                thousands += (current == 0 ? 1 : current) * 1000;
                current = 0;
                foundNumber = true;
                continue;
            }
            if (!NumberWords.TryGetValue(token, out var number))
                return false;
            current += number;
            foundNumber = true;
        }

        value = thousands + current;
        return foundNumber;
    }

    private static string Normalize(string value)
    {
        var decomposed = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var normalized = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                normalized.Append(character);
        }
        return normalized.ToString().Normalize(NormalizationForm.FormC);
    }
}
