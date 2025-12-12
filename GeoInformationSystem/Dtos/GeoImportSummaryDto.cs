namespace GeoAdminDemo.Dtos;

public sealed class GeoImportSummaryDto
{
    public string? Iso3 { get; set; }
    public int MaxLevel { get; set; }
    public int Inserted { get; set; }
    public int Updated { get; set; }
    public int TotalInDb { get; set; }
    public List<string> Errors { get; set; } = [];
}
