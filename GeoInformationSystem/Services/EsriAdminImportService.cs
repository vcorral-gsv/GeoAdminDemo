using GeoAdminDemo.Data;
using GeoAdminDemo.Dtos;
using GeoAdminDemo.Models;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Features;
using NetTopologySuite.IO;
using System.Net;

namespace GeoAdminDemo.Services;

public sealed class EsriAdminImportService(AppDbContext db, IHttpClientFactory httpClientFactory)
{
    private const string BaseUrl =
        "https://server.maps.imb.org/arcgis/rest/services/Hosted/GlobalAdminBoundaries/FeatureServer";

    private readonly HttpClient _http = httpClientFactory.CreateClient("esri-admin");
    private readonly GeoJsonReader _geoJsonReader = new();
    private readonly WKTWriter _wktWriter = new();

    // Ajustes de robustez
    private const int DefaultPageSize = 500;
    private const int Admin4PageSize = 250;
    private const int MaxRetries = 4;

    // ------------------------------
    // IMPORTACIÓN COMPLETA (resumen)
    // ------------------------------
    public async Task<GeoImportSummaryDto> ImportAllAsync(bool hardReset, string? iso3Filter, int maxLevel, CancellationToken ct)
    {
        var summary = new GeoImportSummaryDto { Iso3 = iso3Filter, MaxLevel = maxLevel };

        if (hardReset)
            await db.AdminAreas.ExecuteDeleteAsync(ct);

        var (i0, u0) = await ImportLevel0Async(ct);
        summary.Inserted += i0;
        summary.Updated += u0;

        var countries = await db.AdminAreas.AsNoTracking()
            .Where(x => x.Level == 0 && (iso3Filter == null || x.CountryIso3 == iso3Filter))
            .Select(x => x.CountryIso3)
            .ToListAsync(ct);

        foreach (var iso3 in countries)
        {
            for (var level = 1; level <= maxLevel; level++)
            {
                try
                {
                    var (ins, upd) = await ImportLevelForCountryAsync(level, iso3, ct);
                    summary.Inserted += ins;
                    summary.Updated += upd;
                }
                catch (Exception ex)
                {
                    summary.Errors.Add($"Import {iso3} level {level}: {ex.Message}");
                }
            }
        }

        summary.TotalInDb = await db.AdminAreas.CountAsync(ct);
        return summary;
    }

    // ------------------------------
    // LEVEL 0 (PAÍSES)
    // ------------------------------
    private async Task<(int inserted, int updated)> ImportLevel0Async(CancellationToken ct)
    {
        var inserted = 0;
        var updated = 0;

        var features = await QueryLayerAllAsync(
            layer: 0,
            where: "1=1",
            outFields: "adm0_cd,adm0_nm,level_label",
            returnGeometry: false,
            ct);

        var existing = await db.AdminAreas
            .Where(x => x.Level == 0)
            .ToDictionaryAsync(x => x.Code, ct);

        foreach (var f in features)
        {
            var iso3 = GetAttr(f, "adm0_cd");
            var name = GetAttr(f, "adm0_nm");
            var label = GetAttr(f, "level_label");

            if (string.IsNullOrWhiteSpace(iso3) || string.IsNullOrWhiteSpace(name))
                continue;

            if (!existing.TryGetValue(iso3, out var row))
            {
                db.AdminAreas.Add(new AdminArea
                {
                    CountryIso3 = iso3,
                    Level = 0,
                    Code = iso3,
                    Name = name,
                    LevelLabel = label
                });
                inserted++;
                continue;
            }

            if (!string.Equals(row.Name, name, StringComparison.Ordinal) ||
                !string.Equals(row.LevelLabel, label, StringComparison.Ordinal))
            {
                row.Name = name;
                row.LevelLabel = label;
                row.UpdatedAt = DateTimeOffset.UtcNow;
                updated++;
            }
        }

        await db.SaveChangesAsync(ct);
        return (inserted, updated);
    }

    // ------------------------------
    // LEVEL 1..N (por país)
    // ------------------------------
    private async Task<(int inserted, int updated)> ImportLevelForCountryAsync(int level, string iso3, CancellationToken ct)
    {
        var inserted = 0;
        var updated = 0;

        var features = await QueryLayerAllAsync(
            layer: level,
            where: $"adm0_cd='{iso3}'",
            outFields: $"adm0_cd,adm{level}_cd,adm{level}_nm,adm{level - 1}_cd,level_label",
            returnGeometry: true,
            ct);

        var parents = await db.AdminAreas
            .Where(x => x.CountryIso3 == iso3 && x.Level == level - 1)
            .ToDictionaryAsync(x => x.Code, ct);

        var existing = await db.AdminAreas
            .Where(x => x.CountryIso3 == iso3 && x.Level == level)
            .ToDictionaryAsync(x => x.Code, ct);

        foreach (var f in features)
        {
            var code = GetAttr(f, $"adm{level}_cd") ?? GetAttr(f, $"adm{level}_nm");
            var name = GetAttr(f, $"adm{level}_nm");
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name)) continue;

            var parentCode = GetAttr(f, $"adm{level - 1}_cd");
            parents.TryGetValue(parentCode ?? "", out var parent);

            var levelLabel = GetAttr(f, "level_label");
            var geom = f.Geometry;
            geom?.SRID = 4326; // crítico

            var wkt = geom is not null ? _wktWriter.Write(geom) : null;

            if (!existing.TryGetValue(code, out var row))
            {
                db.AdminAreas.Add(new AdminArea
                {
                    CountryIso3 = iso3,
                    Level = level,
                    Code = code,
                    Name = name,
                    ParentId = parent?.Id,
                    LevelLabel = levelLabel,
                    Geometry = geom,
                    GeometryWkt = wkt
                });
                inserted++;
                continue;
            }

            var changed =
                row.ParentId != parent?.Id ||
                !string.Equals(row.Name, name, StringComparison.Ordinal) ||
                !string.Equals(row.LevelLabel, levelLabel, StringComparison.Ordinal) ||
                !string.Equals(row.GeometryWkt, wkt, StringComparison.Ordinal);

            if (changed)
            {
                row.ParentId = parent?.Id;
                row.Name = name;
                row.LevelLabel = levelLabel;
                row.Geometry = geom;
                row.GeometryWkt = wkt;
                row.UpdatedAt = DateTimeOffset.UtcNow;
                updated++;
            }
        }

        await db.SaveChangesAsync(ct);
        return (inserted, updated);
    }

    // ------------------------------
    // QUERY PAGINADA + RETRIES
    // ------------------------------
    private async Task<List<IFeature>> QueryLayerAllAsync(
        int layer,
        string where,
        string outFields,
        bool returnGeometry,
        CancellationToken ct)
    {
        var pageSize = layer >= 4 ? Admin4PageSize : DefaultPageSize;
        var all = new List<IFeature>();
        var offset = 0;

        while (true)
        {
            var url =
                $"{BaseUrl}/{layer}/query" +
                $"?where={WebUtility.UrlEncode(where)}" +
                $"&outFields={WebUtility.UrlEncode(outFields)}" +
                $"&returnGeometry={(returnGeometry ? "true" : "false")}" +
                (returnGeometry ? "&outSR=4326" : "") +
                $"&resultOffset={offset}" +
                $"&resultRecordCount={pageSize}" +
                $"&f=geojson";

            var json = await GetWithRetryAsync(url, ct);
            var fc = _geoJsonReader.Read<FeatureCollection>(json);
            var batch = fc.ToList();

            if (batch.Count == 0) break;

            all.AddRange(batch);
            offset += batch.Count;

            if (batch.Count < pageSize) break;
        }

        return all;
    }


    private async Task<string> GetWithRetryAsync(string url, CancellationToken ct)
    {
        Exception? last = null;

        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

                if ((int)resp.StatusCode is 429 or 502 or 503 or 504)
                {
                    last = new HttpRequestException($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}", null, resp.StatusCode);
                    await DelayBackoffAsync(attempt, ct);
                    continue;
                }

                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsStringAsync(ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                last = new TimeoutException("Timeout HTTP");
                await DelayBackoffAsync(attempt, ct);
            }
            catch (Exception ex)
            {
                last = ex;
                await DelayBackoffAsync(attempt, ct);
            }
        }

        throw last ?? new Exception("HTTP failed");
    }

    private static Task DelayBackoffAsync(int attempt, CancellationToken ct)
    {
        // Exponencial con jitter suave
        // 300ms, 900ms, 2700ms, 8100ms + jitter [0..150ms]
        var baseMs = 300 * (int)Math.Pow(3, attempt - 1);
        var jitter = Random.Shared.Next(0, 150);
        return Task.Delay(baseMs + jitter, ct);
    }

    private static string? GetAttr(IFeature f, string key)
        => f.Attributes.Exists(key) ? f.Attributes[key]?.ToString() : null;
}
