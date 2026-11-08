namespace SmsService.Api.Contracts;

public record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);
