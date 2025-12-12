using NetTopologySuite.Geometries;

namespace GeoAdminDemo.Models;

public class AdminArea
{
    public long Id { get; set; }

    public required string CountryIso3 { get; set; }   // ESP, USA...
    public int Level { get; set; }                     // 0..4

    public long? ParentId { get; set; }
    public AdminArea? Parent { get; set; }
    public ICollection<AdminArea> Children { get; set; } = new List<AdminArea>();

    public required string Code { get; set; }          // adm{n}_cd
    public required string Name { get; set; }          // adm{n}_nm

    public string? LevelLabel { get; set; }

    // ✅ Spatial REAL en SQL Server (geography)
    public Geometry? Geometry { get; set; }

    // ✅ Solo debug/inspección (NO para resolver)
    public string? GeometryWkt { get; set; }

    public string Source { get; set; } = "ESRI GlobalAdminBoundaries";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
