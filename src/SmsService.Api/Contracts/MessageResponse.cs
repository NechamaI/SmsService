using SmsService.Api.Domain;

namespace SmsService.Api.Contracts;

public record MessageResponse(
    Guid Id,
    string To,
    string Body,
    MessageStatus Status,
    int Attempts,
    string? LastError,
    int Segments,
    SmsEncoding Encoding,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? SentAt)
{
    public static MessageResponse From(Message m) => new(
        m.Id, m.To, m.Body, m.Status, m.Attempts, m.LastError,
        m.Segments, m.Encoding, m.CreatedAt, m.UpdatedAt, m.SentAt);
}
