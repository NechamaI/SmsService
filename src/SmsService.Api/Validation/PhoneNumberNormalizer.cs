using System.Diagnostics.CodeAnalysis;

namespace SmsService.Api.Validation;

/// <summary>
/// Validates Israeli mobile numbers and normalizes them to E.164 (+9725XXXXXXXX).
/// </summary>
public static class PhoneNumberNormalizer
{
    public const string ErrorMessage =
        "Invalid phone number. Expected an Israeli mobile number, e.g. 052-1234567 or +972521234567.";

    // Longest first, so "00972" isn't mistaken for something else.
    private static readonly string[] CountryPrefixes = ["00972", "+972", "972"];

    public static bool TryNormalize(string? input, [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        // 1. Strip the separators people commonly type. Dots are deliberately not allowed.
        var digits = new string(input.Where(c => c is not (' ' or '-' or '(' or ')')).ToArray());

        // 2. Convert an international prefix to the local trunk zero: +97252... -> 052...
        foreach (var prefix in CountryPrefixes)
        {
            if (digits.StartsWith(prefix, StringComparison.Ordinal))
            {
                digits = "0" + digits[prefix.Length..];
                break;
            }
        }

        // 3. Must be exactly 05 + 8 digits. ASCII only: char.IsDigit would accept e.g. Arabic-Indic digits.
        if (digits.Length != 10 || !digits.StartsWith("05", StringComparison.Ordinal) || !digits.All(IsAsciiDigit))
        {
            return false;
        }

        normalized = "+972" + digits[1..];
        return true;
    }

    private static bool IsAsciiDigit(char c) => c is >= '0' and <= '9';
}
