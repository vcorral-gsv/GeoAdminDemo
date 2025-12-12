using Microsoft.EntityFrameworkCore;

namespace GeoAdminDemo.Data.QueryTypes;

[Keyless]
public sealed class AdminAreaCteRow
{
    public long Id { get; set; }
    public long? ParentId { get; set; }
    public int Level { get; set; }
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? LevelLabel { get; set; }
}
