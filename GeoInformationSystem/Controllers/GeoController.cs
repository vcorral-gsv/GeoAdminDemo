using GeoAdminDemo.Data;
using GeoAdminDemo.Dtos;
using GeoAdminDemo.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GeoAdminDemo.Controllers;

[ApiController]
[Route("api/geo")]
public sealed class GeoController(
    AppDbContext db,
    EsriAdminImportService importService,
    GeoResolveService resolveService,
    ArcgisGeocodingService geocoding
) : ControllerBase
{
    // ----------------------------
    // IMPORT
    // ----------------------------
    [HttpPost("import/esri")]
    [ProducesResponseType(typeof(GeoImportSummaryDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<GeoImportSummaryDto>> Import(
        [FromQuery] bool hardReset = false,
        [FromQuery] string? iso3 = null,
        [FromQuery] int maxLevel = 4,
        CancellationToken ct = default)
    {
        if (maxLevel < 0 || maxLevel > 25)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid maxLevel", detail: "maxLevel debe estar entre 0 y 25.");

        if (!string.IsNullOrWhiteSpace(iso3))
            iso3 = iso3.Trim().ToUpperInvariant();

        var summary = await importService.ImportAllAsync(hardReset, iso3, maxLevel, ct);
        return Ok(summary);
    }

    // ----------------------------
    // LISTADO (paginado + filtros)
    // ----------------------------
    [HttpGet("admin-areas")]
    [ProducesResponseType(typeof(PagedResultDto<AdminAreaListItemDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResultDto<AdminAreaListItemDto>>> GetAdminAreas(
        [FromQuery] string? countryIso3,
        [FromQuery] int? level,
        [FromQuery] long? parentId,
        [FromQuery] string? q,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        if (skip < 0) skip = 0;
        take = Math.Clamp(take, 1, 200);

        var query = db.AdminAreas.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(countryIso3))
        {
            countryIso3 = countryIso3.Trim().ToUpperInvariant();
            query = query.Where(x => x.CountryIso3 == countryIso3);
        }

        if (level.HasValue)
            query = query.Where(x => x.Level == level.Value);

        if (parentId.HasValue)
            query = query.Where(x => x.ParentId == parentId.Value);

        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = $"%{q}%";
            query = query.Where(x =>
                EF.Functions.Like(x.Name, pattern) ||
                EF.Functions.Like(x.Code, pattern));
        }

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderBy(x => x.Name)
            .Skip(skip)
            .Take(take)
            .Select(x => new AdminAreaListItemDto
            {
                Id = x.Id,
                Level = x.Level,
                ParentId = x.ParentId,
                Code = x.Code,
                Name = x.Name,
                LevelLabel = x.LevelLabel
            })
            .ToListAsync(ct);

        return Ok(new PagedResultDto<AdminAreaListItemDto>
        {
            Items = items,
            Total = total,
            Skip = skip,
            Take = take
        });
    }

    [HttpGet("admin-areas/{id:long}")]
    [ProducesResponseType(typeof(AdminAreaDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdminAreaDetailDto>> GetAdminArea(long id, CancellationToken ct)
    {
        var item = await db.AdminAreas.AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new AdminAreaDetailDto
            {
                Id = x.Id,
                CountryIso3 = x.CountryIso3,
                Level = x.Level,
                ParentId = x.ParentId,
                Code = x.Code,
                Name = x.Name,
                LevelLabel = x.LevelLabel,
                Source = x.Source,
                UpdatedAt = x.UpdatedAt,
                HasGeometry = x.Geometry != null
            })
            .FirstOrDefaultAsync(ct);

        return item is null ? NotFound() : Ok(item);
    }

    // ✅ GeoJSON (opcional: simplificado)
    [HttpGet("admin-areas/{id:long}/geometry-geojson")]
    [ProducesResponseType(typeof(AdminAreaGeoJsonDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdminAreaGeoJsonDto>> GetGeometryGeoJson(
        long id,
        [FromQuery] double? zoom = null,
        [FromQuery] double? toleranceMeters = null,
        CancellationToken ct = default)
    {
        var dto = await resolveService.GetGeometryGeoJsonAsync(id, zoom, toleranceMeters, ct);
        return dto is null ? (ActionResult<AdminAreaGeoJsonDto>)NotFound() : dto.GeoJson is null ? NoContent() : Ok(dto);
    }

    // Mantén WKT solo si quieres debug. Si no lo necesitas, bórralo.
    [HttpGet("admin-areas/{id:long}/geometry")]
    [ProducesResponseType(typeof(AdminAreaGeometryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdminAreaGeometryDto>> GetGeometry(long id, CancellationToken ct)
    {
        var row = await db.AdminAreas.AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new { x.Id, x.CountryIso3, x.Level, x.Code, x.Name, x.GeometryWkt })
            .FirstOrDefaultAsync(ct);

        return row is null
            ? (ActionResult<AdminAreaGeometryDto>)NotFound()
            : string.IsNullOrWhiteSpace(row.GeometryWkt)
            ? NoContent()
            : Ok(new AdminAreaGeometryDto
            {
                Id = row.Id,
                CountryIso3 = row.CountryIso3,
                Level = row.Level,
                Code = row.Code,
                Name = row.Name,
                GeometryWkt = row.GeometryWkt
            });
    }

    // ----------------------------
    // RESOLVE
    // ----------------------------
    [HttpGet("resolve-point")]
    [ProducesResponseType(typeof(ResolvePointResponseDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<ResolvePointResponseDto>> ResolvePoint(
        [FromQuery] double lat,
        [FromQuery] double lon,
        [FromQuery] string iso3,
        CancellationToken ct)
    {
        if (lat is < -90 or > 90)
            return Problem(statusCode: 400, title: "Invalid lat", detail: "lat debe estar entre -90 y 90.");

        if (lon is < -180 or > 180)
            return Problem(statusCode: 400, title: "Invalid lon", detail: "lon debe estar entre -180 y 180.");

        if (string.IsNullOrWhiteSpace(iso3))
            return Problem(statusCode: 400, title: "Invalid iso3", detail: "iso3 es obligatorio.");

        iso3 = iso3.Trim().ToUpperInvariant();

        var result = await resolveService.ResolvePointSqlAsync(lat, lon, iso3, ct);
        return Ok(result);
    }

    [HttpPost("resolve-from-address")]
    [ProducesResponseType(typeof(ResolveFromAddressResponseDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<ResolveFromAddressResponseDto>> ResolveFromAddress(
        [FromBody] ResolveFromAddressRequestDto req,
        CancellationToken ct)
    {
        if (req is null)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request", detail: "Body obligatorio.");

        if (string.IsNullOrWhiteSpace(req.Address))
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid address", detail: "Address es obligatorio.");

        if (string.IsNullOrWhiteSpace(req.Iso3))
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid iso3", detail: "Iso3 es obligatorio por ahora (para evitar ambigüedad mundial).");

        req.Iso3 = req.Iso3.Trim().ToUpperInvariant();

        var (lat, lon) = await geocoding.GeocodeAsync(req.Address, req.Language, ct);
        var result = await resolveService.ResolvePointSqlAsync(lat, lon, req.Iso3, ct);

        return Ok(new ResolveFromAddressResponseDto
        {
            Address = req.Address,
            Lat = lat,
            Lon = lon,
            Result = result
        });
    }

    // ----------------------------
    // INSPECCIÓN
    // ----------------------------
    [HttpGet("admin-summary/{iso3}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> GetSummary(string iso3, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(iso3))
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid iso3", detail: "iso3 es obligatorio.");

        iso3 = iso3.Trim().ToUpperInvariant();

        var levels = await db.AdminAreas.AsNoTracking()
            .Where(x => x.CountryIso3 == iso3)
            .GroupBy(x => x.Level)
            .Select(g => new
            {
                level = g.Key,
                count = g.Count(),
                levelLabels = g.Select(x => x.LevelLabel).Where(x => x != null).Distinct().ToList()
            })
            .OrderBy(x => x.level)
            .ToListAsync(ct);

        return Ok(new { iso3, levels });
    }

    [HttpGet("resolve-point-cte")]
    [ProducesResponseType(typeof(ResolvePointResponseDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<ResolvePointResponseDto>> ResolvePointCte(
    [FromQuery] double lat,
    [FromQuery] double lon,
    [FromQuery] string iso3,
    CancellationToken ct)
    {
        if (lat is < -90 or > 90)
            return Problem(statusCode: 400, title: "Invalid lat", detail: "lat debe estar entre -90 y 90.");

        if (lon is < -180 or > 180)
            return Problem(statusCode: 400, title: "Invalid lon", detail: "lon debe estar entre -180 y 180.");

        if (string.IsNullOrWhiteSpace(iso3))
            return Problem(statusCode: 400, title: "Invalid iso3", detail: "iso3 es obligatorio.");

        iso3 = iso3.Trim().ToUpperInvariant();

        var result = await resolveService.ResolvePointSqlCteAsync(lat, lon, iso3, ct);
        return Ok(result);
    }

}
