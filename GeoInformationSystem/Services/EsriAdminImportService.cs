using GeoAdminDemo.Data;
using GeoAdminDemo.Dtos;
using GeoAdminDemo.Models;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;

namespace GeoAdminDemo.Services;

public sealed class EsriAdminImportService(AppDbContext db, IHttpClientFactory httpClientFactory)
{
    private readonly HttpClient _http = httpClientFactory.CreateClient("esri-admin");

    private const string BaseUrl =
        "https://server.maps.imb.org/arcgis/rest/services/Hosted/GlobalAdminBoundaries/FeatureServer";

    private readonly GeoJsonReader _geoJsonReader = new();
    private readonly GeoJsonWriter _geoJsonWriter = new();
    private readonly WKTWriter _wktWriter = new();

    // Robustez
    private const int MaxRetries = 4;

    // Import 2 pasadas: ids -> features
    private const int DefaultIdsBatchSize = 200;
    private const int Admin4PlusIdsBatchSize = 50;

    // Circuit breaker
    private const int CircuitBreakerLevelThreshold = 4;
    private const int CircuitBreakerFailureThreshold = 3;

    private const int MaxErrorPayloadChars = 1500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // ------------------------------
    // IMPORTACIÓN COMPLETA (resumen)
    // ------------------------------
    public async Task<GeoImportSummaryDto> ImportAllAsync(bool hardReset, string? iso3Filter, int maxLevel, CancellationToken ct)
    {
        var swTotal = System.Diagnostics.Stopwatch.StartNew();

        var summary = new GeoImportSummaryDto
        {
            Iso3Filter = iso3Filter,
            MaxLevel = maxLevel
        };

        if (hardReset)
            await db.AdminAreas.ExecuteDeleteAsync(ct);

        // level 0 (países)
        var (i0, u0) = await ImportLevel0Async(ct);
        summary.Inserted += i0;
        summary.Updated += u0;

        // Países a importar: si iso3Filter es null => TODOS
        var countries = await db.AdminAreas.AsNoTracking()
            .Where(x => x.Level == 0 && (iso3Filter == null || x.CountryIso3 == iso3Filter))
            .Select(x => x.CountryIso3)
            .ToListAsync(ct);

        // Pre-crea summaries por país
        var byIso = new Dictionary<string, GeoImportCountrySummaryDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var iso in countries)
        {
            var c = new GeoImportCountrySummaryDto { Iso3 = iso };
            byIso[iso] = c;
            summary.Countries.Add(c);
        }

        foreach (var iso3 in countries)
        {
            var swCountry = System.Diagnostics.Stopwatch.StartNew();

            var breaker = new CountryCircuitState(iso3);

            for (var level = 1; level <= maxLevel; level++)
            {
                if (breaker.IsOpen)
                    break;

                try
                {
                    var (ins, upd) = await ImportLevelForCountryAsync(level, iso3, breaker, summary, ct);

                    summary.Inserted += ins;
                    summary.Updated += upd;

                    byIso[iso3].Inserted += ins;
                    byIso[iso3].Updated += upd;
                    byIso[iso3].LevelsImported.Add(level);

                }
                catch (CircuitBreakerOpenException cb)
                {
                    byIso[iso3].CircuitBreakerOpened = true;
                    byIso[iso3].CircuitBreakerOpenedAtLevel = cb.Level;

                    // También lo reflejamos como error tipado
                    summary.Errors.Add(new GeoImportErrorDto
                    {
                        Iso3 = cb.Iso3,
                        Level = cb.Level,
                        Stage = "circuit_breaker",
                        Message = cb.Message,
                        Payload = cb.Payload
                    });

                    break; // corta país
                }
                catch (ImportStepException ie)
                {
                    summary.Errors.Add(ie.ToDto());
                    // seguimos al siguiente level
                }
                catch (Exception ex)
                {
                    summary.Errors.Add(new GeoImportErrorDto
                    {
                        Iso3 = iso3,
                        Level = level,
                        Stage = "unknown",
                        Message = ex.Message
                    });
                }
            }
            swCountry.Stop();
            byIso[iso3].DurationMs = swCountry.ElapsedMilliseconds;
            // total en DB por país al finalizar
            byIso[iso3].TotalInDb = await db.AdminAreas.AsNoTracking()
                .Where(x => x.CountryIso3 == iso3)
                .CountAsync(ct);
        }
        swTotal.Stop();
        summary.DurationMs = swTotal.ElapsedMilliseconds;
        summary.TotalInDb = await db.AdminAreas.CountAsync(ct);
        return summary;
    }

    // ------------------------------
    // LEVEL 0 (PAÍSES) - 2 PASADAS
    // ------------------------------
    private async Task<(int inserted, int updated)> ImportLevel0Async(CancellationToken ct)
    {
        const int level = 0;

        var inserted = 0;
        var updated = 0;

        var objectIds = await QueryObjectIdsAsync(
            layer: level,
            where: "1=1",
            breaker: null,
            currentLayer: level,
            summary: null,
            ct: ct);

        var features = await FetchFeaturesByObjectIdsAsync(
            layer: level,
            objectIds: objectIds,
            outFields: "adm0_cd,adm0_nm,level_label",
            returnGeometry: false,
            outSr: null,
            breaker: null,
            summary: null,
            ct: ct);

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
    private async Task<(int inserted, int updated)> ImportLevelForCountryAsync(
        int level,
        string iso3,
        CountryCircuitState breaker,
        GeoImportSummaryDto summary,
        CancellationToken ct)
    {
        var inserted = 0;
        var updated = 0;

        // 1) IDs
        var objectIds = await QueryObjectIdsAsync(
            layer: level,
            where: $"adm0_cd='{iso3}'",
            breaker: breaker,
            currentLayer: level,
            summary: summary,
            ct: ct);

        // 2) Features
        var outFields = "*";

        var features = await FetchFeaturesByObjectIdsAsync(
            layer: level,
            objectIds: objectIds,
            outFields: outFields,
            returnGeometry: true,
            outSr: 4326,
            breaker: breaker,
            summary: summary,
            ct: ct);

        // Dump attribute schema for debugging (levels 0..3)
        if (level is >= 0 and <= 3)
        {
            try
            {
                var dir = Path.Combine(AppContext.BaseDirectory, "DataExports");
                Directory.CreateDirectory(dir);
                var dumpPath = Path.Combine(dir, $"adm{level}_{iso3}_fields.json");

                // Collect unique attribute names and sample values
                var fieldSamples = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in features)
                {
                    var names = f.Attributes.GetNames();
                    foreach (var n in names)
                    {
                        if (!fieldSamples.ContainsKey(n))
                        {
                            var v = f.Attributes[n]?.ToString();
                            fieldSamples[n] = v;
                        }
                    }
                }

                var dumpObj = new
                {
                    iso3,
                    level,
                    count = features.Count,
                    fields = fieldSamples.OrderBy(k => k.Key).ToDictionary(k => k.Key, k => k.Value)
                };
                var dumpJson = System.Text.Json.JsonSerializer.Serialize(dumpObj, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(dumpPath, dumpJson);
            }
            catch { /* ignore dump errors */ }
        }

        var parents = await db.AdminAreas
            .Where(x => x.CountryIso3 == iso3 && x.Level == level - 1)
            .ToDictionaryAsync(x => x.Code, ct);

        var existing = await db.AdminAreas
            .Where(x => x.CountryIso3 == iso3 && x.Level == level)
            .ToDictionaryAsync(x => x.Code, ct);

        // Prepare CSV rows for levels 2 (provincias) and 3 (municipios)
        var csvRows = new List<string>();
        var exportCsv = level is 2 or 3;
        var csvHeader = "Des,PaisId,ShapeId,Geometry";

        foreach (var f in features)
        {
            var code = GetAttr(f, $"adm{level}_cd") ?? GetAttr(f, $"adm{level}_nm");
            var name = GetAttr(f, $"adm{level}_nm");
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name)) continue;

            var parentCode = GetAttr(f, $"adm{level - 1}_cd");
            parents.TryGetValue(parentCode ?? "", out var parent);

            var levelLabel = GetAttr(f, "level_label");

            var geom = f.Geometry;
            geom?.SRID = geom.SRID == 0 ? 4326 : 4326;

            // Normalize polygon ring orientation for SQL Server geography
            if (geom is not null)
            {
                geom = NormalizeGeographyPolygonOrientation(geom);
                EnsureSrid4326(geom);
            }

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
            }
            else
            {
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

            if (exportCsv)
            {
                // Prefer GlobalID, then OBJECTID, then code
                var shapeId = GetAttr(f, "GlobalID") ?? GetAttr(f, "OBJECTID") ?? code;
                // GeoJSON geometry text
                var geometryJson = geom is not null ? _geoJsonWriter.Write(geom) : "";

                var des = EscapeCsv(name);
                var paisId = EscapeCsv(MapPaisId(iso3));
                var shapeIdCsv = EscapeCsv(shapeId);
                var geometryText = EscapeCsv(geometryJson);
                csvRows.Add($"{des},{paisId},{shapeIdCsv},{geometryText}");
            }
        }

        await db.SaveChangesAsync(ct);

        if (exportCsv && csvRows.Count > 0)
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "DataExports");
            Directory.CreateDirectory(dir);
            var fileName = level == 2 ? $"adm2_{iso3}.csv" : $"adm3_{iso3}.csv";
            var path = Path.Combine(dir, fileName);

            var writeHeader = !File.Exists(path);
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, Encoding.UTF8);
            if (writeHeader)
                writer.WriteLine(csvHeader);
            foreach (var row in csvRows)
                writer.WriteLine(row);
        }

        return (inserted, updated);
    }

    private static string MapPaisId(string iso3)
    {
        return iso3;
    }

    // ============================================================
    // 1) IDs: returnIdsOnly=true (f=json)
    // ============================================================
    private async Task<IReadOnlyList<long>> QueryObjectIdsAsync(
        int layer,
        string where,
        CountryCircuitState? breaker,
        int currentLayer,
        GeoImportSummaryDto? summary,
        CancellationToken ct)
    {
        var url = $"{BaseUrl}/{layer}/query";

        var form = new Dictionary<string, string>
        {
            ["f"] = "json",
            ["where"] = where,
            ["returnIdsOnly"] = "true",
            ["returnCountOnly"] = "false"
        };

        var json = await PostFormWithRetryAsync(url, form, breaker, currentLayer, summary, stage: "ids", ct);

        var dto = JsonSerializer.Deserialize<ArcGisIdsResponse>(json, JsonOptions);

        if (dto?.Error is not null)
        {
            var msg = BuildArcGisErrorMessage(dto.Error, json);
            throw new ImportStepException(
                iso3: breaker?.Iso3 ?? "ALL",
                level: currentLayer,
                stage: "ids",
                message: msg,
                payload: Truncate(json),
                arcErr: dto.Error);
        }

        return dto?.ObjectIds?.Select(x => (long)x).ToArray() ?? [];
    }

    // ============================================================
    // 2) Features: objectIds en batches (POST) (f=geojson)
    // ============================================================
    private async Task<List<IFeature>> FetchFeaturesByObjectIdsAsync(
        int layer,
        IReadOnlyList<long> objectIds,
        string outFields,
        bool returnGeometry,
        int? outSr,
        CountryCircuitState? breaker,
        GeoImportSummaryDto? summary,
        CancellationToken ct)
    {
        if (objectIds.Count == 0) return [];

        var batchSize = layer >= 4 ? Admin4PlusIdsBatchSize : DefaultIdsBatchSize;
        var all = new List<IFeature>(capacity: Math.Min(objectIds.Count, 10_000));

        breaker?.ResetStreakOnLayerChange(layer);

        for (var i = 0; i < objectIds.Count; i += batchSize)
        {
            var batchIds = objectIds.Skip(i).Take(batchSize).ToArray();

            var url = $"{BaseUrl}/{layer}/query";

            var form = new Dictionary<string, string>
            {
                ["f"] = "geojson",
                ["objectIds"] = string.Join(",", batchIds),
                ["outFields"] = outFields,
                ["returnGeometry"] = returnGeometry ? "true" : "false"
            };

            if (returnGeometry && outSr.HasValue)
                form["outSR"] = outSr.Value.ToString();

            var json = await PostFormWithRetryAsync(url, form, breaker, layer, summary, stage: "features", ct);

            // 200 con {"error":{...}}
            if (TryParseArcGisError(json, out var arcErr))
            {
                breaker?.OnRequestFailure(layer);

                var msg = BuildArcGisErrorMessage(arcErr!, json);

                // circuit breaker (si aplica)
                var opened = breaker?.TryOpenBreaker(layer, msg, payload: Truncate(json));
                if (opened is not null) throw opened;

                throw new ImportStepException(
                    iso3: breaker?.Iso3 ?? "ALL",
                    level: layer,
                    stage: "features",
                    message: msg,
                    payload: Truncate(json),
                    arcErr: arcErr);
            }

            FeatureCollection fc;
            try
            {
                fc = _geoJsonReader.Read<FeatureCollection>(json);
            }
            catch (Exception ex)
            {
                breaker?.OnRequestFailure(layer);

                var msg = $"GeoJSON parse error: {ex.Message}";
                var payload = Truncate(json);

                var opened = breaker?.TryOpenBreaker(layer, msg, payload);
                if (opened is not null) throw opened;

                throw new ImportStepException(
                    iso3: breaker?.Iso3 ?? "ALL",
                    level: layer,
                    stage: "parse",
                    message: msg,
                    payload: payload,
                    arcErr: null,
                    inner: ex);
            }

            breaker?.OnRequestSuccess(layer);
            all.AddRange([.. fc]);
        }

        return all;
    }

    // ============================================================
    // HTTP: POST form + retry/backoff SOLO 502/503/504
    // ============================================================
    private async Task<string> PostFormWithRetryAsync(
        string url,
        Dictionary<string, string> form,
        CountryCircuitState? breaker,
        int level,
        GeoImportSummaryDto? summary,
        string stage,
        CancellationToken ct)
    {
        Exception? last = null;

        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new FormUrlEncodedContent(form)
                };

                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);

                // Retry SOLO 502/503/504
                if ((int)resp.StatusCode is 502 or 503 or 504)
                {
                    breaker?.OnRequestFailure(level);

                    last = new HttpRequestException(
                        $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}",
                        null,
                        resp.StatusCode);

                    var opened = breaker?.TryOpenBreaker(level, last.Message, payload: Truncate(body));
                    if (opened is not null) throw opened;

                    await DelayBackoffAsync(attempt, ct);
                    continue;
                }

                if (!resp.IsSuccessStatusCode)
                {
                    breaker?.OnRequestFailure(level);

                    // NO retry para 500/4xx
                    // pero propagamos payload
                    _ = TryParseArcGisError(body, out ArcGisError? arcErr);

                    var msg = $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}. Payload: {Truncate(body)}";
                    if (arcErr is not null)
                        msg = $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} | ArcGIS: {BuildArcGisErrorMessage(arcErr, body)}";

                    var opened = breaker?.TryOpenBreaker(level, msg, payload: Truncate(body));
                    if (opened is not null) throw opened;

                    throw new ImportStepException(
                        iso3: breaker?.Iso3 ?? "ALL",
                        level: level,
                        stage: stage,
                        message: msg,
                        payload: Truncate(body),
                        httpStatus: (int)resp.StatusCode,
                        httpReason: resp.ReasonPhrase,
                        arcErr: arcErr);
                }

                return body;
            }
            catch (CircuitBreakerOpenException)
            {
                throw;
            }
            catch (ImportStepException)
            {
                throw;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Regla estricta: no retry genérico.
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                throw;
            }
        }

        throw last ?? new Exception("HTTP failed");
    }

    private static Task DelayBackoffAsync(int attempt, CancellationToken ct)
    {
        var baseMs = 300 * (int)Math.Pow(3, attempt - 1);
        var jitter = Random.Shared.Next(0, 150);
        return Task.Delay(baseMs + jitter, ct);
    }

    // ============================================================
    // Helpers
    // ============================================================
    private static string? GetAttr(IFeature f, string key)
    {
        if (f.Attributes.Exists(key)) return f.Attributes[key]?.ToString();
        // Case-insensitive fallback
        try
        {
            var names = f.Attributes.GetNames();
            for (int i = 0; i < names.Length; i++)
            {
                if (string.Equals(names[i], key, StringComparison.OrdinalIgnoreCase))
                {
                    return f.Attributes[names[i]]?.ToString();
                }
            }
        }
        catch { }
        return null;
    }

    private static bool TryParseArcGisError(string json, out ArcGisError? err)
    {
        err = null;
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            var dto = JsonSerializer.Deserialize<ArcGisErrorEnvelope>(json, JsonOptions);
            if (dto?.Error is null) return false;
            err = dto.Error;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildArcGisErrorMessage(ArcGisError err, string rawPayload)
    {
        var details = err.Details is { Length: > 0 }
            ? $" Details: {string.Join(" | ", err.Details)}"
            : "";

        return $"code={err.Code}, message={err.Message}.{details} Payload: {Truncate(rawPayload)}";
    }

    private static string Truncate(string? s)
    {
        return string.IsNullOrWhiteSpace(s) ? "" : s.Length <= MaxErrorPayloadChars ? s : s[..MaxErrorPayloadChars] + "…(truncated)";
    }

    private static string EscapeCsv(string s)
    {
        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? '"' + s.Replace("\"", "\"\"") + '"' : s;
    }

    // Normalize polygon/multipolygon orientation for SQL Server geography
    private static Geometry NormalizeGeographyPolygonOrientation(Geometry geometry)
    {
        if (geometry is Polygon p)
            return NormalizePolygon(p);
        if (geometry is MultiPolygon mp)
        {
            var factory = geometry.Factory;
            var polys = new Polygon[mp.NumGeometries];
            for (int i = 0; i < mp.NumGeometries; i++)
                polys[i] = NormalizePolygon((Polygon)mp.GetGeometryN(i));
            return factory.CreateMultiPolygon(polys);
        }
        if (geometry is GeometryCollection gc)
        {
            var factory = geometry.Factory;
            var geoms = new Geometry[gc.NumGeometries];
            for (int i = 0; i < gc.NumGeometries; i++)
                geoms[i] = NormalizeGeographyPolygonOrientation(gc.GetGeometryN(i));
            return factory.CreateGeometryCollection(geoms);
        }
        return geometry;

        static Polygon NormalizePolygon(Polygon poly)
        {
            var factory = poly.Factory;

            // Exterior ring: must be CCW
            var shell = (LinearRing)poly.ExteriorRing;
            var fixedShell = EnsureRingOrientation(shell, ccw: true);

            // Holes: must be CW
            var holes = new LinearRing[poly.NumInteriorRings];
            for (int i = 0; i < poly.NumInteriorRings; i++)
            {
                var hole = (LinearRing)poly.GetInteriorRingN(i);
                holes[i] = EnsureRingOrientation(hole, ccw: false);
            }

            return factory.CreatePolygon(fixedShell, holes);
        }

        static LinearRing EnsureRingOrientation(LinearRing ring, bool ccw)
        {
            var coords = ring.Coordinates;
            bool isCcw = IsCcw(coords);
            return ccw ? isCcw ? ring : (LinearRing)ring.Reverse() : isCcw ? (LinearRing)ring.Reverse() : ring;
        }

        static bool IsCcw(Coordinate[] coords)
        {
            // Shoelace formula signed area: >0 => CCW, <0 => CW
            double area2 = 0; // 2 * area
            for (int i = 0, j = (coords.Length - 1); i < coords.Length; j = i, i++)
            {
                area2 += (coords[j].X * coords[i].Y) - (coords[i].X * coords[j].Y);
            }
            return area2 > 0;
        }
    }

    private static void EnsureSrid4326(Geometry g)
    {
        if (g is null) return;
        g.SRID = 4326;
        switch (g)
        {
            case Polygon p:
                p.ExteriorRing.SRID = 4326;
                for (int i = 0; i < p.NumInteriorRings; i++)
                    p.GetInteriorRingN(i).SRID = 4326;
                break;
            case MultiPolygon mp:
                for (int i = 0; i < mp.NumGeometries; i++)
                    EnsureSrid4326(mp.GetGeometryN(i));
                break;
            case GeometryCollection gc:
                for (int i = 0; i < gc.NumGeometries; i++)
                    EnsureSrid4326(gc.GetGeometryN(i));
                break;
            case LineString ls:
                ls.SRID = 4326;
                break;
            case Point pt:
                pt.SRID = 4326;
                break;
        }
    }

    // ============================================================
    // DTOs ArcGIS
    // ============================================================
    private sealed class ArcGisIdsResponse
    {
        [JsonPropertyName("objectIds")]
        public int[]? ObjectIds { get; set; }

        [JsonPropertyName("error")]
        public ArcGisError? Error { get; set; }
    }

    private sealed class ArcGisErrorEnvelope
    {
        [JsonPropertyName("error")]
        public ArcGisError? Error { get; set; }
    }

    private sealed class ArcGisError
    {
        [JsonPropertyName("code")]
        public int Code { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = "";

        [JsonPropertyName("details")]
        public string[]? Details { get; set; }
    }

    // ============================================================
    // Exceptions tipadas para “mandarlas” al summary
    // ============================================================
    private sealed class ImportStepException(
        string iso3,
        int level,
        string stage,
        string message,
        string? payload,
        int? httpStatus = null,
        string? httpReason = null,
EsriAdminImportService.ArcGisError? arcErr = null,
        Exception? inner = null) : Exception(message, inner)
    {
        public string Iso3 { get; } = iso3;
        public int Level { get; } = level;
        public string Stage { get; } = stage;
        public int? HttpStatus { get; } = httpStatus;
        public string? HttpReason { get; } = httpReason;
        public ArcGisError? ArcErr { get; } = arcErr;
        public string? Payload { get; } = payload;

        public GeoImportErrorDto ToDto()
            => new()
            {
                Iso3 = Iso3,
                Level = Level,
                Stage = Stage,
                HttpStatus = HttpStatus,
                HttpReason = HttpReason,
                ArcGisCode = ArcErr?.Code,
                ArcGisMessage = ArcErr?.Message,
                ArcGisDetails = ArcErr?.Details,
                Payload = Payload,
                Message = Message
            };
    }

    // ============================================================
    // Circuit breaker por país
    // ============================================================
    private sealed class CountryCircuitState(string iso3)
    {
        public string Iso3 { get; } = iso3;
        public bool IsOpen { get; private set; }

        private int _currentLayer = -1;
        private int _failureStreak = 0;

        public void ResetStreakOnLayerChange(int layer)
        {
            if (_currentLayer != layer)
            {
                _currentLayer = layer;
                _failureStreak = 0;
            }
        }

        public void OnRequestSuccess(int layer)
        {
            if (_currentLayer != layer) _currentLayer = layer;
            _failureStreak = 0;
        }

        public void OnRequestFailure(int layer)
        {
            if (_currentLayer != layer) _currentLayer = layer;
            _failureStreak++;
        }

        public CircuitBreakerOpenException? TryOpenBreaker(int layer, string reason, string? payload)
        {
            if (IsOpen) return new CircuitBreakerOpenException(Iso3, layer, reason, payload);

            if (layer < CircuitBreakerLevelThreshold)
                return null;

            if (_failureStreak >= CircuitBreakerFailureThreshold)
            {
                IsOpen = true;
                return new CircuitBreakerOpenException(
                    Iso3,
                    layer,
                    $"Circuit breaker OPEN after {_failureStreak} consecutive failures. Last: {reason}",
                    payload);
            }

            return null;
        }
    }

    private sealed class CircuitBreakerOpenException(string iso3, int level, string message, string? payload) : Exception(message)
    {
        public string Iso3 { get; } = iso3;
        public int Level { get; } = level;
        public string? Payload { get; } = payload;
    }
}
