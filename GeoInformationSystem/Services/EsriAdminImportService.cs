using GeoAdminDemo.Data;
using GeoAdminDemo.Dtos;
using GeoAdminDemo.Models;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Features;
using NetTopologySuite.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeoAdminDemo.Services;

public sealed class EsriAdminImportService
{
    private readonly AppDbContext _db;
    private readonly HttpClient _http;

    private const string BaseUrl =
        "https://server.maps.imb.org/arcgis/rest/services/Hosted/GlobalAdminBoundaries/FeatureServer";

    private readonly GeoJsonReader _geoJsonReader = new();
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

    public EsriAdminImportService(AppDbContext db, IHttpClientFactory httpClientFactory)
    {
        _db = db;
        _http = httpClientFactory.CreateClient("esri-admin");
    }

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
            await _db.AdminAreas.ExecuteDeleteAsync(ct);

        // level 0 (países)
        var (i0, u0) = await ImportLevel0Async(ct);
        summary.Inserted += i0;
        summary.Updated += u0;

        // Países a importar: si iso3Filter es null => TODOS
        var countries = await _db.AdminAreas.AsNoTracking()
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
            byIso[iso3].TotalInDb = await _db.AdminAreas.AsNoTracking()
                .Where(x => x.CountryIso3 == iso3)
                .CountAsync(ct);
        }
        swTotal.Stop();
        summary.DurationMs = swTotal.ElapsedMilliseconds;
        summary.TotalInDb = await _db.AdminAreas.CountAsync(ct);
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

        var existing = await _db.AdminAreas
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
                _db.AdminAreas.Add(new AdminArea
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

        await _db.SaveChangesAsync(ct);
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
        var outFields = $"adm0_cd,adm{level}_cd,adm{level}_nm,adm{level - 1}_cd,level_label";

        var features = await FetchFeaturesByObjectIdsAsync(
            layer: level,
            objectIds: objectIds,
            outFields: outFields,
            returnGeometry: true,
            outSr: 4326,
            breaker: breaker,
            summary: summary,
            ct: ct);

        var parents = await _db.AdminAreas
            .Where(x => x.CountryIso3 == iso3 && x.Level == level - 1)
            .ToDictionaryAsync(x => x.Code, ct);

        var existing = await _db.AdminAreas
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
            geom?.SRID = geom.SRID == 0 ? 4326 : 4326;

            var wkt = geom is not null ? _wktWriter.Write(geom) : null;

            if (!existing.TryGetValue(code, out var row))
            {
                _db.AdminAreas.Add(new AdminArea
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

        await _db.SaveChangesAsync(ct);
        return (inserted, updated);
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

        return dto?.ObjectIds?.Select(x => (long)x).ToArray() ?? Array.Empty<long>();
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
            all.AddRange(fc.ToList());
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
                    TryParseArcGisError(body, out var arcErr);

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
        => f.Attributes.Exists(key) ? f.Attributes[key]?.ToString() : null;

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
    private sealed class ImportStepException : Exception
    {
        public string Iso3 { get; }
        public int Level { get; }
        public string Stage { get; }
        public int? HttpStatus { get; }
        public string? HttpReason { get; }
        public ArcGisError? ArcErr { get; }
        public string? Payload { get; }

        public ImportStepException(
            string iso3,
            int level,
            string stage,
            string message,
            string? payload,
            int? httpStatus = null,
            string? httpReason = null,
            ArcGisError? arcErr = null,
            Exception? inner = null)
            : base(message, inner)
        {
            Iso3 = iso3;
            Level = level;
            Stage = stage;
            Payload = payload;
            HttpStatus = httpStatus;
            HttpReason = httpReason;
            ArcErr = arcErr;
        }

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
    private sealed class CountryCircuitState
    {
        public string Iso3 { get; }
        public bool IsOpen { get; private set; }

        private int _currentLayer = -1;
        private int _failureStreak = 0;

        public CountryCircuitState(string iso3) => Iso3 = iso3;

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

    private sealed class CircuitBreakerOpenException : Exception
    {
        public string Iso3 { get; }
        public int Level { get; }
        public string? Payload { get; }

        public CircuitBreakerOpenException(string iso3, int level, string message, string? payload)
            : base(message)
        {
            Iso3 = iso3;
            Level = level;
            Payload = payload;
        }
    }
}
