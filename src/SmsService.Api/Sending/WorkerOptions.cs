namespace SmsService.Api.Sending;

/// <summary>Bound from the "Worker" config section.</summary>
public sealed class WorkerOptions
{
    public const string SectionName = "Worker";

    public bool Enabled { get; set; } = true;

    /// <summary>How many due messages to pick up per tick.</summary>
    public int BatchSize { get; set; } = 20;
}
