using GeoAdminDemo.Data;
using GeoAdminDemo.Dtos;
using GeoAdminDemo.Data.QueryTypes;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NetTopologySuite.Simplify;

namespace GeoAdminDemo.Services;

public sealed class GeoResolveService(AppDbContext db)
{
    private static readonly GeometryFactory GeometryFactory4326 =
        NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);

    private static readonly GeoJsonWriter GeoJsonWriter = new();

    /// <summary>
    /// Resolves an administrative path for a point using iterative parent lookups.
    /// </summary>
    public async Task<ResolvePointResponseDto> ResolvePointSqlAsync(
        double lat,
        double lon,
        string iso3,
        CancellationToken ct)
    {
        iso3 = iso3.Trim().ToUpperInvariant();

        var point = GeometryFactory4326.CreatePoint(new Coordinate(lon, lat));

        var leaf = await db.AdminAreas.AsNoTracking()
            .Where(a => a.CountryIso3 == iso3 && a.Geometry != null)
            .Where(a => a.Geometry!.Intersects(point))
            .OrderByDescending(a => a.Level)
            .Select(a => new { a.Id, a.ParentId, a.Level, a.Code, a.Name, a.LevelLabel })
            .FirstOrDefaultAsync(ct);

        if (leaf is null)
        {
            return new ResolvePointResponseDto
            {
                CountryIso3 = iso3,
                Path =
                [
                    new ResolvedAdminAreaDto { Level = 0, Code = iso3, Name = iso3 }
                ]
            };
        }

        var pathReversed = new List<ResolvedAdminAreaDto>();
        long? currentId = leaf.Id;

        while (currentId is not null)
        {
            var node = await db.AdminAreas.AsNoTracking()
                .Where(a => a.Id == currentId.Value)
                .Select(a => new ResolvedAdminAreaDto
                {
                    Id = a.Id,
                    Level = a.Level,
                    ParentId = a.ParentId,
                    Code = a.Code,
                    Name = a.Name,
                    LevelLabel = a.LevelLabel
                })
                .FirstOrDefaultAsync(ct);

            if (node is null) break;

            pathReversed.Add(node);
            currentId = node.ParentId;
        }

        pathReversed.Reverse();

        if (pathReversed.Count == 0 || pathReversed[0].Level != 0)
            pathReversed.Insert(0, new ResolvedAdminAreaDto { Level = 0, Code = iso3, Name = iso3 });

        return new ResolvePointResponseDto { CountryIso3 = iso3, Path = pathReversed };
    }

    /// <summary>
    /// Resolves an administrative path using a recursive CTE for parent traversal in a single roundtrip.
    /// </summary>
    public async Task<ResolvePointResponseDto> ResolvePointSqlCteAsync(
        double lat,
        double lon,
        string iso3,
        CancellationToken ct)
    {
        iso3 = iso3.Trim().ToUpperInvariant();

        var point = GeometryFactory4326.CreatePoint(new Coordinate(lon, lat));

        // 1) leaf (igual que antes)
        var leafId = await db.AdminAreas.AsNoTracking()
            .Where(a => a.CountryIso3 == iso3 && a.Geometry != null)
            .Where(a => a.Geometry!.Intersects(point))
            .OrderByDescending(a => a.Level)
            .Select(a => (long?)a.Id)
            .FirstOrDefaultAsync(ct);

        if (leafId is null)
        {
            return new ResolvePointResponseDto
            {
                CountryIso3 = iso3,
                Path =
                [
                    new ResolvedAdminAreaDto { Level = 0, Code = iso3, Name = iso3 }
                ]
            };
        }

        // 2) CTE: leaf -> padres (una sola query)
        var nodes = await db.AdminAreaCteRows
            .FromSqlInterpolated($@"
WITH cte AS (
    SELECT
        Id,
        ParentId,
        Level,
        Code,
        Name,
        LevelLabel
    FROM [AdminAreas]
    WHERE Id = {leafId.Value}

    UNION ALL

    SELECT
        a.Id,
        a.ParentId,
        a.Level,
        a.Code,
        a.Name,
        a.LevelLabel
    FROM [AdminAreas] a
    INNER JOIN cte ON a.Id = cte.ParentId
)
SELECT
    Id,
    ParentId,
    Level,
    Code,
    Name,
    LevelLabel
FROM cte;
")
            .AsNoTracking()
            .ToListAsync(ct);


        // 3) ordenar root -> leaf
        // El CTE devuelve sin orden garantizado. Ordenamos por Level asc.
        // (En tu modelo Level sube con la profundidad, así que sirve.)
        var ordered = nodes
            .OrderBy(n => n.Level)
            .Select(n => new ResolvedAdminAreaDto
            {
                Id = n.Id,
                ParentId = n.ParentId,
                Level = n.Level,
                Code = n.Code,
                Name = n.Name,
                LevelLabel = n.LevelLabel
            })
            .ToList();

        // 4) asegurar nivel 0
        if (ordered.Count == 0 || ordered[0].Level != 0)
            ordered.Insert(0, new ResolvedAdminAreaDto { Level = 0, Code = iso3, Name = iso3 });

        return new ResolvePointResponseDto { CountryIso3 = iso3, Path = ordered };
    }

    /// <summary>
    /// Returns GeoJSON for an admin area, optionally simplified for map zoom/tolerance.
    /// </summary>
    public async Task<AdminAreaGeoJsonDto?> GetGeometryGeoJsonAsync(
        long id,
        double? zoom,
        double? toleranceMeters,
        CancellationToken ct)
    {
        var row = await db.AdminAreas.AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new
            {
                x.Id,
                x.CountryIso3,
                x.Level,
                x.Code,
                x.Name,
                x.Geometry
            })
            .FirstOrDefaultAsync(ct);

        if (row is null) return null;

        if (row.Geometry is null)
        {
            return new AdminAreaGeoJsonDto
            {
                Id = row.Id,
                CountryIso3 = row.CountryIso3,
                Level = row.Level,
                Code = row.Code,
                Name = row.Name,
                GeoJson = null,
                Zoom = zoom,
                SimplifyToleranceMeters = null
            };
        }

        var geom = row.Geometry;

        if (geom.SRID == 0) geom.SRID = 4326;

        double? usedTolerance = null;

        if (!toleranceMeters.HasValue && zoom.HasValue)
            usedTolerance = ZoomToToleranceMeters(zoom.Value);
        else if (toleranceMeters.HasValue)
            usedTolerance = Math.Max(0, toleranceMeters.Value);

        if (usedTolerance is > 0)
        {
            var degTol = MetersToDegreesApprox(usedTolerance.Value);
            geom = TopologyPreservingSimplifier.Simplify(geom, degTol);
            geom.SRID = 4326;
        }

        var feature = new NetTopologySuite.Features.Feature(
            geom,
            new NetTopologySuite.Features.AttributesTable
            {
                { "id", row.Id },
                { "level", row.Level },
                { "code", row.Code },
                { "name", row.Name },
                { "iso3", row.CountryIso3 }
            });

        var geoJson = GeoJsonWriter.Write(feature);

        return new AdminAreaGeoJsonDto
        {
            Id = row.Id,
            CountryIso3 = row.CountryIso3,
            Level = row.Level,
            Code = row.Code,
            Name = row.Name,
            GeoJson = geoJson,
            Zoom = zoom,
            SimplifyToleranceMeters = usedTolerance
        };
    }

    private static double ZoomToToleranceMeters(double zoom) => zoom switch
    {
        <= 3 => 20_000,
        <= 5 => 10_000,
        <= 7 => 5_000,
        <= 9 => 1_000,
        <= 11 => 300,
        <= 13 => 80,
        _ => 15
    };

    private static double MetersToDegreesApprox(double meters)
    {
        const double metersPerDegLat = 111_320.0;
        return Math.Max(0, meters / metersPerDegLat);
    }
}
