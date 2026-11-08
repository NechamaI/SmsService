namespace SmsService.Api.Contracts;

/// <summary>
/// Request body for POST /messages. Both fields are nullable on purpose: a missing field is reported
/// by <see cref="Validation.CreateMessageValidator"/> with our own message, not by ASP.NET's implicit [Required].
/// </summary>
public record CreateMessageRequest(string? To, string? Body);
