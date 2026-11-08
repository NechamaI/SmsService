using SmsService.Api.Domain;

namespace SmsService.Api.Contracts;

/// <summary>Body of the 202 response to POST /messages.</summary>
public record CreateMessageResponse(Guid Id, MessageStatus Status);
