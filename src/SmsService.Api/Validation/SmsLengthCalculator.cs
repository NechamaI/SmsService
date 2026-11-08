using SmsService.Api.Domain;

namespace SmsService.Api.Validation;

/// <summary>
/// Picks the SMS encoding for a message and counts its length and segments.
/// It doesn't reject anything; the caller decides what to do with <see cref="SmsLengthResult.IsWithinLimit"/>.
/// </summary>
public static class SmsLengthCalculator
{
    public const int MaxSegments = 3;

    // GSM 03.38 basic character set (each char = 1 septet). ESC (0x1B) is deliberately excluded.
    private const string Basic =
        "@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞÆæßÉ !\"#¤%&'()*+,-./0123456789:;<=>?" +
        "¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà";

    // GSM 03.38 extension table: sent as ESC + char, so each costs 2 septets.
    private const string Extension = "\f^{}\\[~]|€";

    private static readonly HashSet<char> BasicSet = [.. Basic];
    private static readonly HashSet<char> ExtensionSet = [.. Extension];

    private const int Gsm7Single = 160, Gsm7Multi = 153;
    private const int Ucs2Single = 70, Ucs2Multi = 67;

    public static SmsLengthResult Calculate(string body)
    {
        var gsm7Length = TryCountGsm7(body);

        return gsm7Length is { } length
            ? Build(SmsEncoding.GSM7, length, Gsm7Single, Gsm7Multi)
            // Simplification: an extension char straddling a segment boundary isn't accounted for.
            : Build(SmsEncoding.UCS2, body.Length, Ucs2Single, Ucs2Multi);
    }

    /// <summary>Returns the length in septets, or null if any char isn't representable in GSM7.</summary>
    private static int? TryCountGsm7(string body)
    {
        var length = 0;
        foreach (var c in body)
        {
            if (BasicSet.Contains(c)) length += 1;
            else if (ExtensionSet.Contains(c)) length += 2;
            else return null;
        }
        return length;
    }

    private static SmsLengthResult Build(SmsEncoding encoding, int length, int single, int multi)
    {
        var segments = length <= single ? 1 : (int)Math.Ceiling(length / (double)multi);
        return new SmsLengthResult(encoding, length, segments, MaxLength: multi * MaxSegments);
    }
}
