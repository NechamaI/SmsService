using Microsoft.AspNetCore.Mvc.ModelBinding;
using SmsService.Api.Contracts;

namespace SmsService.Api.Validation;

/// <param name="To">The phone number, already normalized to E.164.</param>
public record ValidatedMessage(string To, string Body, SmsLengthResult Length);

public static class CreateMessageValidator
{
    public const string BodyRequiredMessage = "Message body is required.";

    /// <summary>
    /// Validates both fields and adds every error to <paramref name="errors"/> under "to" / "body".
    /// Returns null if anything is invalid.
    /// </summary>
    public static ValidatedMessage? Validate(CreateMessageRequest request, ModelStateDictionary errors)
    {
        if (!PhoneNumberNormalizer.TryNormalize(request.To, out var to))
        {
            errors.AddModelError("to", PhoneNumberNormalizer.ErrorMessage);
        }

        SmsLengthResult? length = null;
        if (string.IsNullOrWhiteSpace(request.Body))
        {
            errors.AddModelError("body", BodyRequiredMessage);
        }
        else
        {
            length = SmsLengthCalculator.Calculate(request.Body);
            if (!length.IsWithinLimit)
            {
                errors.AddModelError("body",
                    $"Message is {length.Length} characters long ({length.Encoding}); the maximum is {length.MaxLength}.");
            }
        }

        // The body is stored exactly as sent (no trim).
        return to is not null && length is { IsWithinLimit: true }
            ? new ValidatedMessage(to, request.Body!, length)
            : null;
    }
}
