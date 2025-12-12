namespace GeoAdminDemo.Dtos;

public sealed class PagedResultDto<T>
{
    public required IReadOnlyList<T> Items { get; init; }
    public required int Total { get; init; }
    public required int Skip { get; init; }
    public required int Take { get; init; }
}
