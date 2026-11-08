using SmsService.Api.Domain;
using SmsService.Api.Validation;

namespace SmsService.Tests;

public class SmsLengthCalculatorTests
{
    private static SmsLengthResult R(string body) => SmsLengthCalculator.Calculate(body);

    // ---- GSM7 boundaries: 160 single, 153 per part, max 3 parts = 459 ----

    [Fact]
    public void Gsm7_160_chars_is_one_segment()
    {
        var r = R(new string('a', 160));
        Assert.Equal(SmsEncoding.GSM7, r.Encoding);
        Assert.Equal(160, r.Length);
        Assert.Equal(1, r.Segments);
        Assert.True(r.IsWithinLimit);
    }

    [Fact]
    public void Gsm7_161_chars_is_two_segments() => Assert.Equal(2, R(new string('a', 161)).Segments);

    [Fact]
    public void Gsm7_306_chars_is_two_segments() => Assert.Equal(2, R(new string('a', 306)).Segments);

    [Fact]
    public void Gsm7_307_chars_is_three_segments() => Assert.Equal(3, R(new string('a', 307)).Segments);

    [Fact]
    public void Gsm7_459_chars_is_three_segments_and_within_limit()
    {
        var r = R(new string('a', 459));
        Assert.Equal(3, r.Segments);
        Assert.True(r.IsWithinLimit);
    }

    [Fact]
    public void Gsm7_460_chars_is_over_limit()
    {
        var r = R(new string('a', 460));
        Assert.False(r.IsWithinLimit);
        Assert.Equal(459, r.MaxLength);
    }

    // ---- UCS2 boundaries: 70 single, 67 per part, max 3 parts = 201 ----

    [Fact]
    public void Ucs2_70_chars_is_one_segment()
    {
        var r = R(new string('א', 70));
        Assert.Equal(SmsEncoding.UCS2, r.Encoding);
        Assert.Equal(1, r.Segments);
    }

    [Fact]
    public void Ucs2_71_chars_is_two_segments() => Assert.Equal(2, R(new string('א', 71)).Segments);

    [Fact]
    public void Ucs2_201_chars_is_three_segments_and_within_limit()
    {
        var r = R(new string('א', 201));
        Assert.Equal(3, r.Segments);
        Assert.True(r.IsWithinLimit);
    }

    [Fact]
    public void Ucs2_202_chars_is_over_limit()
    {
        var r = R(new string('א', 202));
        Assert.False(r.IsWithinLimit);
        Assert.Equal(201, r.MaxLength);
    }

    // ---- GSM7 extension table: each char costs 2 septets ----

    [Theory]
    [InlineData("€")]
    [InlineData("{")]
    public void Extension_char_counts_as_two(string body)
    {
        var r = R(body);
        Assert.Equal(SmsEncoding.GSM7, r.Encoding);
        Assert.Equal(2, r.Length);
    }

    [Fact]
    public void Extension_char_pushes_160_over_single_segment()
    {
        var r = R(new string('a', 159) + "€");
        Assert.Equal(161, r.Length);
        Assert.Equal(2, r.Segments);
    }

    // ---- Encoding detection ----

    [Fact]
    public void One_hebrew_char_makes_the_message_ucs2() =>
        Assert.Equal(SmsEncoding.UCS2, R(new string('a', 100) + "א").Encoding);

    [Fact]
    public void Emoji_is_ucs2_and_counts_as_two()
    {
        var r = R("😀");
        Assert.Equal(SmsEncoding.UCS2, r.Encoding);
        Assert.Equal(2, r.Length);
    }

    [Fact]
    public void Newline_and_basic_symbols_stay_gsm7() =>
        Assert.Equal(SmsEncoding.GSM7, R("Hello\nWorld @£$").Encoding);
}
