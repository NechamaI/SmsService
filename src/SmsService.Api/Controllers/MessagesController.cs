using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmsService.Api.Contracts;
using SmsService.Api.Data;
using SmsService.Api.Domain;
using SmsService.Api.Validation;

namespace SmsService.Api.Controllers;

[ApiController]
[Route("messages")]
public class MessagesController(AppDbContext db, TimeProvider time) : ControllerBase
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    /// <summary>Accepts a message for background sending.</summary>
    [HttpPost]
    [ProducesResponseType<CreateMessageResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create(CreateMessageRequest request, CancellationToken ct)
    {
        var validated = CreateMessageValidator.Validate(request, ModelState);
        if (validated is null)
        {
            return ValidationProblem(ModelState);
        }

        var now = time.GetUtcNow().UtcDateTime;
        var message = new Message
        {
            // Id is left empty: EF Core generates a sequential GUID, which keeps the clustered index from fragmenting.
            To = validated.To,
            Body = validated.Body,
            Encoding = validated.Length.Encoding,
            Segments = validated.Length.Segments,
            Status = MessageStatus.Pending,
            Attempts = 0,
            NextAttemptAt = now, // the worker may pick it up right away
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Messages.Add(message);
        await db.SaveChangesAsync(ct);

        // 202: sending happens in the background, so the client doesn't wait on the provider.
        return AcceptedAtAction(nameof(GetById), new { id = message.Id },
            new CreateMessageResponse(message.Id, message.Status));
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<MessageResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var message = await db.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);
        return message is null ? NotFound() : Ok(MessageResponse.From(message));
    }

    /// <summary>Lists messages, newest first, optionally filtered by status.</summary>
    [HttpGet]
    [ProducesResponseType<PagedResponse<MessageResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> List(
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DefaultPageSize,
        CancellationToken ct = default)
    {
        MessageStatus? statusFilter = null;
        if (status is not null)
        {
            // Match the enum names exactly (case-insensitive). Enum.TryParse would also accept "1" and "Pending,Sent".
            var name = Enum.GetNames<MessageStatus>()
                .FirstOrDefault(n => string.Equals(n, status, StringComparison.OrdinalIgnoreCase));
            if (name is null)
            {
                ModelState.AddModelError("status",
                    $"Invalid status. Allowed values: {string.Join(", ", Enum.GetNames<MessageStatus>())}.");
            }
            else
            {
                statusFilter = Enum.Parse<MessageStatus>(name);
            }
        }

        if (page < 1)
        {
            ModelState.AddModelError("page", "page must be 1 or greater.");
        }

        // Out-of-range values are rejected, not silently clamped, so the client knows its request was wrong.
        if (pageSize is < 1 or > MaxPageSize)
        {
            ModelState.AddModelError("pageSize", $"pageSize must be between 1 and {MaxPageSize}.");
        }
        // Skip = (page - 1) * pageSize must fit in an int; otherwise it wraps negative and the DB query fails with 500.
        else if (page >= 1 && (long)(page - 1) * pageSize > int.MaxValue)
        {
            ModelState.AddModelError("page", "page is too large.");
        }

        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var query = db.Messages.AsNoTracking();
        if (statusFilter is { } s)
        {
            query = query.Where(m => m.Status == s);
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(m => m.CreatedAt)
            .ThenByDescending(m => m.Id) // unique tie-breaker: stable pages when CreatedAt is equal
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return Ok(new PagedResponse<MessageResponse>(items.Select(MessageResponse.From).ToList(), page, pageSize, total));
    }
}
