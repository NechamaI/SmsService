using SmsService.Api.Domain;

namespace SmsService.Api.Validation;

/// <param name="Length">GSM7: septets (extension char = 2). UCS2: UTF-16 code units.</param>
/// <param name="MaxLength">The longest message allowed for this encoding (3 segments).</param>
public record SmsLengthResult(SmsEncoding Encoding, int Length, int Segments, int MaxLength)
{
    public bool IsWithinLimit => Length <= MaxLength;
}
