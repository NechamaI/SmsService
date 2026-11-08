using SmsService.Api.Validation;

namespace SmsService.Tests;

public class PhoneNumberNormalizerTests
{
    [Theory]
    [InlineData("0521234567")]
    [InlineData("052-123-4567")]
    [InlineData("(052) 123 4567")]
    [InlineData("+972521234567")]
    [InlineData("+972 52-123-4567")]
    [InlineData("972521234567")]
    [InlineData("00972521234567")]
    public void Valid_formats_normalize_to_E164(string input)
    {
        Assert.True(PhoneNumberNormalizer.TryNormalize(input, out var normalized));
        Assert.Equal("+972521234567", normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("02-1234567")]       // landline
    [InlineData("+97221234567")]     // landline, international
    [InlineData("052123456")]        // too short
    [InlineData("05212345678")]      // too long
    [InlineData("05212a4567")]       // letters
    [InlineData("052.123.4567")]     // dots are not stripped
    [InlineData("+9720521234567")]   // trunk zero after the country code
    [InlineData("٠٥٢١٢٣٤٥٦٧")]       // Arabic-Indic digits: char.IsDigit accepts them, we must not
    public void Invalid_formats_are_rejected(string? input)
    {
        Assert.False(PhoneNumberNormalizer.TryNormalize(input, out _));
    }
}
