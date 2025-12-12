namespace GeoAdminDemo.Dtos;

public class AdminAreaListItemDto
{
    public long Id { get; set; }
    public int Level { get; set; }
    public long? ParentId { get; set; }
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? LevelLabel { get; set; }
}

public sealed class AdminAreaDetailDto : AdminAreaListItemDto
{
    public string CountryIso3 { get; set; } = default!;
    public string? Source { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public bool HasGeometry { get; set; }
}

public sealed class AdminAreaGeometryDto
{
    public long Id { get; set; }
    public string CountryIso3 { get; set; } = default!;
    public int Level { get; set; }
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string GeometryWkt { get; set; } = default!;
}

public sealed class ResolvePointResponseDto
{
    public string CountryIso3 { get; set; } = default!;
    public List<ResolvedAdminAreaDto> Path { get; set; } = [];
}

public sealed class ResolvedAdminAreaDto
{
    public long? Id { get; set; }
    public int Level { get; set; }
    public long? ParentId { get; set; }
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? LevelLabel { get; set; }

    public bool ExistsInDb => Id is > 0;
}

public sealed class ResolveFromAddressRequestDto
{
    public required string Address { get; set; }
    public string? Iso3 { get; set; }
    public string? Language { get; set; }
}

public sealed class ResolveFromAddressResponseDto
{
    public string Address { get; set; } = default!;
    public double Lat { get; set; }
    public double Lon { get; set; }
    public ResolvePointResponseDto Result { get; set; } = default!;
}
public sealed class AdminAreaGeoJsonDto
{
    public long Id { get; set; }
    public string CountryIso3 { get; set; } = default!;
    public int Level { get; set; }
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;

    // GeoJSON "Feature" (string JSON)
    public string? GeoJson { get; set; }

    // Info opcional (útil para debug/cliente)
    public double? SimplifyToleranceMeters { get; set; }
    public double? Zoom { get; set; }
}
