namespace SmsService.Api.Domain;

public class Message
{
    public const int ToMaxLength = 16;
    public const int BodyMaxLength = 1000;
    public const int LastErrorMaxLength = 500;
    public const int ProviderMessageIdMaxLength = 100;

    public Guid Id { get; set; }

    /// <summary>Phone number normalized to E.164, e.g. +972521234567.</summary>
    public string To { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;
    public SmsEncoding Encoding { get; set; }
    public int Segments { get; set; }
    public MessageStatus Status { get; set; }
    public int Attempts { get; set; }

    /// <summary>The worker may pick the message up only when this time has passed (UTC).</summary>
    public DateTime NextAttemptAt { get; set; }

    public string? LastError { get; set; }
    public string? ProviderMessageId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? SentAt { get; set; }
}
