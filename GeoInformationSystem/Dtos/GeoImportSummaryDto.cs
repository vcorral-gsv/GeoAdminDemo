namespace GeoAdminDemo.Dtos;

public sealed class GeoImportSummaryDto
{
    // Filtro solicitado (null => TODOS)
    public string? Iso3Filter { get; set; }

    public int MaxLevel { get; set; }

    // Totales globales (suma de todos los países importados)
    public int Inserted { get; set; }
    public int Updated { get; set; }
    public int TotalInDb { get; set; }

    public long DurationMs { get; set; }

    // Resumen por país (si Iso3Filter != null normalmente tendrá 1 item)
    public List<GeoImportCountrySummaryDto> Countries { get; set; } = [];

    // Errores tipados (sin perder payload)
    public List<GeoImportErrorDto> Errors { get; set; } = [];
}

public sealed class GeoImportCountrySummaryDto
{
    public required string Iso3 { get; set; }
    public int Inserted { get; set; }
    public int Updated { get; set; }
    public int TotalInDb { get; set; } // total en DB para ese ISO3 al final del import

    // ✅ nuevo: duración por país (ms)
    public long DurationMs { get; set; }

    // ✅ nuevo: niveles importados (incluye 0 si lo importas por país; normalmente 1..maxLevel)
    public List<int> LevelsImported { get; set; } = [];
    public bool CircuitBreakerOpened { get; set; }
    public int? CircuitBreakerOpenedAtLevel { get; set; }
}

public sealed class GeoImportErrorDto
{
    public required string Iso3 { get; set; }
    public required int Level { get; set; }

    // Fase del import (ids / features / parse / db / etc.)
    public required string Stage { get; set; }

    // HTTP (si aplica)
    public int? HttpStatus { get; set; }
    public string? HttpReason { get; set; }

    // ArcGIS error (si aplica)
    public int? ArcGisCode { get; set; }
    public string? ArcGisMessage { get; set; }
    public string[]? ArcGisDetails { get; set; }

    // Payload bruto (normalmente JSON) truncado
    public string? Payload { get; set; }

    // Mensaje humano final
    public required string Message { get; set; }

    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
}
