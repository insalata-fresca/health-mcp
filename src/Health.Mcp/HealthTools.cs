using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Health.Mcp;

/// <summary>
/// Read-only tools over the operator's aggregated Health Connect data (Garmin +
/// Withings + phone) as exposed by the Google Health API v4. Every tool is a GET;
/// none mutate anything. The <see cref="GoogleHealthClient"/> is DI-injected into
/// each static tool method; the remaining
/// parameters are the MCP tool arguments.
/// </summary>
[McpServerToolType]
public sealed class HealthTools
{
    private static readonly JsonSerializerOptions _json =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>
    /// The paging clause shared by every list tool's description.
    ///
    /// <para>The server already returned <c>nextPageToken</c> and already accepted a
    /// <c>pageToken</c> on the generic reader — but the typed tools took neither, and no
    /// description mentioned either. So the cursor existed end to end and was reachable from
    /// nowhere, and a caller that received a first page had no way to know, or to say, that it was
    /// only the first.</para>
    /// </summary>
    private const string PAGEDOC =
        "Paged: the response carries `nextPageToken` when more data points exist beyond this page — " +
        "pass it back as `pageToken` to continue, and treat its ABSENCE as the only proof you have " +
        "the whole window. `pageSize` caps one page. ";

    [McpServerTool(Name = "list_weight")]
    [Description(
        "List body-weight data points from Google Health (Withings + Garmin + phone, aggregated via " +
        "Health Connect). Read-only. Optional ISO-8601 start/end bound the window when supplied. " +
        PAGEDOC + "Verified data type.")]
    public static Task<string> ListWeight(
        GoogleHealthClient client,
        [Description("Optional ISO-8601 window start (e.g. 2026-07-01T00:00:00Z). Empty = no start bound.")] string start = "",
        [Description("Optional ISO-8601 window end. Empty = no end bound.")] string end = "",
        [Description("Optional page size. Empty/0 = the configured default (1440).")] int pageSize = 0,
        [Description("Optional pageToken from a previous response's nextPageToken.")] string pageToken = "",
        CancellationToken ct = default)
        => ListAsync(client, "weight", start, end, ct, pageSize: pageSize > 0 ? pageSize : null, pageToken: pageToken);

    [McpServerTool(Name = "list_sleep")]
    [Description(
        "List sleep data points from Google Health (Garmin + phone). Read-only. Optional ISO-8601 " +
        "start/end bound the window. " + PAGEDOC + "Verified data type.")]
    public static Task<string> ListSleep(
        GoogleHealthClient client,
        [Description("Optional ISO-8601 window start. Empty = no start bound.")] string start = "",
        [Description("Optional ISO-8601 window end. Empty = no end bound.")] string end = "",
        [Description("Optional page size. Empty/0 = the configured default (1440).")] int pageSize = 0,
        [Description("Optional pageToken from a previous response's nextPageToken.")] string pageToken = "",
        CancellationToken ct = default)
        => ListAsync(client, "sleep", start, end, ct, pageSize: pageSize > 0 ? pageSize : null, pageToken: pageToken);

    [McpServerTool(Name = "list_steps")]
    [Description(
        "List RAW step-count data points from Google Health (Garmin + phone). Read-only. Optional ISO-8601 " +
        "start/end bound the window. NOTE: raw steps arrive as 1-2 minute records AND a phone and a watch " +
        "commonly both record the same walking — so summing these without `source` DOUBLE COUNTS. " +
        "For a daily total use daily_total instead, which aggregates server-side. " + PAGEDOC +
        "NOTE: a source filter is applied AFTER paging, so a page can come back empty while more " +
        "pages still hold matching points — keep following nextPageToken.")]
    public static Task<string> ListSteps(
        GoogleHealthClient client,
        [Description("Optional ISO-8601 window start. Empty = no start bound.")] string start = "",
        [Description("Optional ISO-8601 window end. Empty = no end bound.")] string end = "",
        [Description("Optional source filter to avoid double counting: a formFactor (watch|phone), a platform " +
                     "(HEALTH_CONNECT), or part of the writing app id (e.g. garmin). Empty = all sources.")] string source = "",
        [Description("Optional page size. Empty/0 = the configured default (1440).")] int pageSize = 0,
        [Description("Optional pageToken from a previous response's nextPageToken.")] string pageToken = "",
        CancellationToken ct = default)
        => ListAsync(client, "steps", start, end, ct, source, pageSize > 0 ? pageSize : null, pageToken);

    [McpServerTool(Name = "daily_total")]
    [Description(
        "Server-side DAILY AGGREGATE for a data type (Google Health dataPoints:dailyRollUp) — one row per " +
        "day. This is the right tool for 'how many steps today/this week': it returns the daily total in a " +
        "SINGLE call instead of paging through hundreds of raw 1-2 minute records, and it aggregates " +
        "server-side so it does not double count a phone and a watch. Dates are YYYY-MM-DD, the range is " +
        "closed-open (end is EXCLUSIVE, so use tomorrow's date for today). Max range 90 days for most types, " +
        "14 for heart-rate/active-minutes/total-calories/calories-in-heart-rate-zone. NOT every type rolls " +
        "up — sleep and daily-vo2-max do not (use list_datapoints for those); steps, distance, heart-rate, " +
        "weight, active-energy-burned and run-vo2-max do.")]
    public static async Task<string> DailyTotal(
        GoogleHealthClient client,
        [Description("Data type to aggregate, e.g. steps, distance, active-energy-burned.")] string dataType,
        [Description("Range start, inclusive, as YYYY-MM-DD.")] string start,
        [Description("Range end, EXCLUSIVE, as YYYY-MM-DD. For a single day pass the next day.")] string end,
        [Description("Optional provenance scope: all-sources (default), google-wearables (excludes manual " +
                     "entries), or google-sources. Roll-up rows carry no per-point source, so this is the " +
                     "only provenance control here.")] string sourceFamily = "",
        [Description("Days per aggregation window. Default 1 = one row per day.")] int windowSizeDays = 1,
        CancellationToken ct = default)
    {
        try
        {
            if (!DateOnly.TryParse(start, out var s))
                throw new ArgumentException($"start '{start}' is not a YYYY-MM-DD date.");
            if (!DateOnly.TryParse(end, out var e))
                throw new ArgumentException($"end '{end}' is not a YYYY-MM-DD date.");

            var family = string.IsNullOrWhiteSpace(sourceFamily)
                ? null
                : sourceFamily.Contains('/', StringComparison.Ordinal)
                    ? sourceFamily.Trim()
                    : $"users/me/dataSourceFamilies/{sourceFamily.Trim()}";

            var result = await client.DailyRollUpAsync(dataType, s, e, windowSizeDays, family, ct)
                .ConfigureAwait(false);

            return JsonSerializer.Serialize(new
            {
                dataType,
                range = new { start = s.ToString("yyyy-MM-dd"), end_exclusive = e.ToString("yyyy-MM-dd") },
                sourceFamily = family,
                rollup = result,
            }, _json);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { dataType, error = ex.Message }, _json);
        }
    }

    [McpServerTool(Name = "list_nutrition")]
    [Description(
        "List nutrition-log entries (meals logged via health_log_nutrition) from Google Health API v4. " +
        "Read-only. This is the typed read for the `nutrition-log` data type — use it to verify a write or " +
        "review the day's meals; the generic list_datapoints requires the exact id `nutrition-log` (NOT " +
        "`nutrition`). Optional ISO-8601 start/end bound the window when supplied.")]
    public static Task<string> ListNutrition(
        GoogleHealthClient client,
        [Description("Optional ISO-8601 window start. Empty = no start bound.")] string start = "",
        [Description("Optional ISO-8601 window end. Empty = no end bound.")] string end = "",
        CancellationToken ct = default)
        => ListAsync(client, "nutrition-log", start, end, ct);

    [McpServerTool(Name = "list_datapoints")]
    [Description(
        "Generic read of Google Health API v4 data points for ANY data type string. Read-only. " +
        "dataType examples: weight, sleep, steps (verified), heart_rate / activity types (coverage " +
        "to be confirmed against the live account). Optional ISO-8601 start/end bound the window when " +
        "supplied. " + PAGEDOC +
        "NOTE: a source filter is applied AFTER paging, so a page can come back empty while more " +
        "pages still hold matching points. " +
        "Use the typed tools (health_list_weight/sleep/steps) for the common cases.")]
    public static Task<string> ListDatapoints(
        GoogleHealthClient client,
        [Description("Google Health data type, e.g. weight, sleep, steps, heart_rate.")] string dataType,
        [Description("Optional ISO-8601 window start. Empty = no start bound.")] string start = "",
        [Description("Optional ISO-8601 window end. Empty = no end bound.")] string end = "",
        [Description("Optional source filter: formFactor (watch|phone), platform (HEALTH_CONNECT), or part " +
                     "of the writing app id (e.g. garmin). Empty = all sources.")] string source = "",
        [Description("Optional page size. Empty/0 = the configured default (1440).")] int pageSize = 0,
        [Description("Optional pageToken from a previous response's nextPageToken.")] string pageToken = "",
        CancellationToken ct = default)
        => ListAsync(client, dataType, start, end, ct, source, pageSize > 0 ? pageSize : null, pageToken);

    [McpServerTool(Name = "list_data_types")]
    [Description(
        "List the Google Health data types this MCP is configured to advertise (env GOOGLE_HEALTH_DATA_TYPES). " +
        "Read-only, no upstream call. weight/sleep/steps are verified; others are best-effort until confirmed.")]
    public static string ListDataTypes(HealthOptions opt)
        => JsonSerializer.Serialize(new { data_types = opt.DataTypes }, _json);

    [McpServerTool(Name = "log_nutrition")]
    [Description(
        "Log a nutrition (food) entry to Google Health (nutrition-log). This is the ONLY WRITE " +
        "tool — it creates a data point on your account. Requires the OAuth refresh token to " +
        "carry the googlehealth.nutrition.writeonly scope (see README). Idempotent: a content " +
        "signature (date|mealType|calories|name) is compared against that day's existing " +
        "entries and an equivalent one is NOT re-written (returns status 'duplicate'). Returns " +
        "{status:'ok'|'duplicate'|'disabled'|'not_configured'|'unauthorized'|'unreachable'|'not_supported', ...}.")]
    public static async Task<string> LogNutrition(
        GoogleHealthClient client,
        HealthOptions opt,
        [Description("Total energy of the entry in kilocalories (kcal). Required.")] double calories,
        [Description("Meal category: breakfast | lunch | dinner | snack. Required.")] string mealType,
        [Description("Food display name, e.g. 'Chicken salad'. Optional but recommended.")] string name = "",
        [Description("ISO-8601 time the food was logged (e.g. 2026-07-16T12:30:00Z). Empty = now (UTC).")] string time = "",
        [Description("Protein in grams. Optional — written as a nutrients[] entry (nutrient=PROTEIN).")] double? protein_g = null,
        [Description("Carbohydrate in grams. Optional — written as totalCarbohydrate.")] double? carbs_g = null,
        [Description("Fat in grams. Optional — written as totalFat.")] double? fat_g = null,
        [Description("Optional caller idempotency token. When absent a SHA-256 of date|mealType|calories|name is derived.")] string dedupeKey = "",
        CancellationToken ct = default)
    {
        if (!opt.NutritionWriteEnabled)
            return Err("disabled", "Nutrition writes are disabled (GOOGLE_HEALTH_NUTRITION_WRITE_ENABLED=false).");

        if (string.IsNullOrWhiteSpace(opt.NutritionWrapperKey) || string.IsNullOrWhiteSpace(opt.NutritionDataType))
            return Err("not_configured", "Nutrition wrapper key / dataType is not configured.");

        // Resolve the meal enum via the (env-overridable) map. VERIFIED enum values.
        if (!opt.MealTypeMap.TryGetValue(mealType.Trim(), out var mealEnum))
            return Err("not_supported",
                $"Unknown mealType '{mealType}'. Known: {string.Join(", ", opt.MealTypeMap.Keys)}. " +
                "Override via GOOGLE_HEALTH_MEALTYPE_MAP.");

        // Timestamp → SessionTimeInterval (start<end strictly; UtcOffset as a google-duration in seconds).
        DateTimeOffset ts;
        if (string.IsNullOrWhiteSpace(time))
            ts = DateTimeOffset.UtcNow;
        else if (!DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture,
                     DateTimeStyles.RoundtripKind, out ts))
            return Err("not_supported", $"Could not parse time '{time}' as ISO-8601.");

        var rfc3339 = ts.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        // Google Health requires the interval start to be STRICTLY earlier than end
        // (a point-in-time start==end is rejected: INVALID_TIME_RANGE). We anchor startTime
        // at the logged instant (so the dedup date derives from it) and give endTime a
        // nominal +1min window. Verified live 2026-07-16.
        var endRfc3339 = ts.ToUniversalTime().AddMinutes(1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var offsetDuration = $"{(long)ts.Offset.TotalSeconds}s";
        var dateKey = ts.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var displayName = string.IsNullOrWhiteSpace(name) ? "" : name.Trim();

        // Content signature used for server-side dedup (the API has no user-key field, so
        // dedup is content-based; dedupeKey is an advisory token echoed back to the caller).
        var contentSig = Sha256Hex($"{dateKey}|{mealEnum}|{calories:0.####}|{displayName.ToLowerInvariant()}");
        var derivedKey = string.IsNullOrWhiteSpace(dedupeKey) ? contentSig : dedupeKey.Trim();

        // ── Idempotency: scan that day's existing entries for an equivalent record. ──
        // A read failure (e.g. read scope absent) does NOT block the write; it is noted.
        string? dedupNote = null;
        try
        {
            var existing = await client.ListDataPointsAsync(opt.NutritionDataType, null, null, null, null, ct)
                .ConfigureAwait(false);
            if (existing.ValueKind == JsonValueKind.Object
                && existing.TryGetProperty("dataPoints", out var dps)
                && dps.ValueKind == JsonValueKind.Array)
            {
                foreach (var dp in dps.EnumerateArray())
                {
                    if (!dp.TryGetProperty(opt.NutritionWrapperKey, out var nl)) continue;
                    if (SignatureOf(nl, opt) == contentSig)
                    {
                        var dupId = dp.TryGetProperty("name", out var idEl) ? idEl.GetString() : null;
                        return JsonSerializer.Serialize(new
                        {
                            status = "duplicate",
                            id = dupId,
                            dataType = opt.NutritionDataType,
                            derivedKey,
                            note = "An equivalent entry already exists for this day; not re-written.",
                        }, _json);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            dedupNote = $"dedup pre-check skipped ({ex.Message}); wrote without dedup.";
        }

        // ── Build the DataPoint body (VERIFIED shape; each piece env-overridable). ──
        var record = new Dictionary<string, object?>
        {
            ["interval"] = new Dictionary<string, object?>
            {
                ["startTime"] = rfc3339,
                ["startUtcOffset"] = offsetDuration,
                ["endTime"] = endRfc3339,
                ["endUtcOffset"] = offsetDuration,
            },
            ["mealType"] = mealEnum,
            ["energy"] = Quantity(opt.EnergyValueKey, calories, opt.EnergyUnitEnum),
        };
        if (!string.IsNullOrEmpty(displayName)) record["foodDisplayName"] = displayName;
        if (carbs_g is { } c) record["totalCarbohydrate"] = Quantity(opt.MassValueKey, c, opt.MassUnitEnum);
        if (fat_g is { } f) record["totalFat"] = Quantity(opt.MassValueKey, f, opt.MassUnitEnum);
        if (protein_g is { } p)
            record["nutrients"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["nutrient"] = opt.ProteinNutrient,
                    ["quantity"] = Quantity(opt.MassValueKey, p, opt.MassUnitEnum),
                },
            };

        var body = new Dictionary<string, object?> { [opt.NutritionWrapperKey] = record };

        // ── Write. ──
        try
        {
            var res = await client.CreateDataPointAsync(opt.NutritionDataType, body, ct).ConfigureAwait(false);
            if (res.Success)
            {
                // The create response is a Long-Running-Operation wrapper:
                // {"done":true,"response":{"@type":"...DataPoint","name":"users/.../dataPoints/<id>", ...}}
                // so the created resource id is at response.name, NOT top-level name.
                string? newId = null;
                if (res.Json is { ValueKind: JsonValueKind.Object } j)
                {
                    if (j.TryGetProperty("response", out var resp) && resp.ValueKind == JsonValueKind.Object
                        && resp.TryGetProperty("name", out var rn))
                        newId = rn.GetString();
                    else if (j.TryGetProperty("name", out var n))
                        newId = n.GetString();
                }
                // Honest status semantics: a 2xx create with a real upstream id is "ok";
                // a 2xx with no id we could parse is surfaced distinctly (never a silent id:null "ok").
                return JsonSerializer.Serialize(new
                {
                    status = newId is null ? "ok_unverified" : "ok",
                    id = newId,
                    dataType = opt.NutritionDataType,
                    derivedKey,
                    written = new { calories, mealType = mealEnum, name = displayName, time = rfc3339, protein_g, carbs_g, fat_g },
                    note = dedupNote,
                }, _json);
            }

            var status = res.StatusCode switch
            {
                401 or 403 => "unauthorized",
                404 => "not_supported",
                _ => "unreachable",
            };
            return Err(status, $"Google Health API {res.StatusCode}: {res.Body}", opt.NutritionDataType);
        }
        catch (Exception ex)
        {
            var status = ex.Message.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase)
                         || ex.Message.Contains("401") || ex.Message.Contains("403")
                ? "unauthorized"
                : "unreachable";
            return Err(status, ex.Message, opt.NutritionDataType);
        }
    }

    [McpServerTool(Name = "delete_nutrition")]
    [Description(
        "Delete a nutrition-log entry from Google Health by its full resource name (the `id` returned by " +
        "health_log_nutrition, or a `name` from health_list_nutrition, e.g. " +
        "'users/<uid>/dataTypes/nutrition-log/dataPoints/<id>'). Covered by the nutrition write scope; you can " +
        "only delete entries this integration created. Irreversible — the entry is removed upstream.")]
    public static async Task<string> DeleteNutrition(
        GoogleHealthClient client,
        HealthOptions opt,
        [Description("Full resource name of the entry to delete (from log/list; starts with 'users/').")] string name,
        CancellationToken ct = default)
    {
        if (!opt.NutritionWriteEnabled)
            return Err("disabled", "Nutrition writes are disabled (GOOGLE_HEALTH_NUTRITION_WRITE_ENABLED=false).");
        if (string.IsNullOrWhiteSpace(name) || !name.Contains("/dataPoints/", StringComparison.Ordinal))
            return Err("not_supported",
                "Pass the full resource name from log/list, e.g. users/<uid>/dataTypes/nutrition-log/dataPoints/<id>.");

        try
        {
            var res = await client.BatchDeleteAsync(opt.NutritionDataType, new[] { name.Trim() }, ct).ConfigureAwait(false);
            if (res.Success)
                return JsonSerializer.Serialize(new
                {
                    status = "deleted",
                    id = name.Trim(),
                    dataType = opt.NutritionDataType,
                }, _json);

            var status = res.StatusCode switch
            {
                401 or 403 => "unauthorized",
                404 => "not_found",
                _ => "unreachable",
            };
            return Err(status, $"Google Health API {res.StatusCode}: {res.Body}", opt.NutritionDataType);
        }
        catch (Exception ex)
        {
            var status = ex.Message.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase)
                         || ex.Message.Contains("401") || ex.Message.Contains("403")
                ? "unauthorized"
                : "unreachable";
            return Err(status, ex.Message, opt.NutritionDataType);
        }
    }

    [McpServerTool(Name = "list_hydration")]
    [Description(
        "List hydration-log entries (water/liquid intake) from Google Health API v4. Read-only, " +
        "typed read for the `hydration-log` data type. Optional ISO-8601 start/end bound the window.")]
    public static Task<string> ListHydration(
        GoogleHealthClient client,
        [Description("Optional ISO-8601 window start. Empty = no start bound.")] string start = "",
        [Description("Optional ISO-8601 window end. Empty = no end bound.")] string end = "",
        CancellationToken ct = default)
        => ListAsync(client, "hydration-log", start, end, ct);

    [McpServerTool(Name = "log_hydration")]
    [Description(
        "Log a hydration (liquid intake) entry to Google Health. Writes a hydration-log DataPoint " +
        "(amountConsumed in millilitres). Covered by the same nutrition write scope as food. Returns " +
        "the real upstream id on success.")]
    public static async Task<string> LogHydration(
        GoogleHealthClient client,
        HealthOptions opt,
        [Description("Amount of liquid consumed, in millilitres (e.g. 500). Required.")] double milliliters,
        [Description("ISO-8601 time consumed (e.g. 2026-07-17T09:00:00Z). Empty = now (UTC).")] string time = "",
        CancellationToken ct = default)
    {
        if (!opt.NutritionWriteEnabled)
            return Err("disabled", "Nutrition/hydration writes are disabled (GOOGLE_HEALTH_NUTRITION_WRITE_ENABLED=false).");
        if (milliliters <= 0)
            return Err("not_supported", "milliliters must be > 0.");

        DateTimeOffset ts;
        if (string.IsNullOrWhiteSpace(time))
            ts = DateTimeOffset.UtcNow;
        else if (!DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out ts))
            return Err("not_supported", $"Could not parse time '{time}' as ISO-8601.");

        var startRfc = ts.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        // start strictly < end (INVALID_TIME_RANGE otherwise) — nominal +1min window.
        var endRfc = ts.ToUniversalTime().AddMinutes(1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var offset = $"{(long)ts.Offset.TotalSeconds}s";

        var body = new Dictionary<string, object?>
        {
            ["hydrationLog"] = new Dictionary<string, object?>
            {
                ["interval"] = new Dictionary<string, object?>
                {
                    ["startTime"] = startRfc, ["startUtcOffset"] = offset,
                    ["endTime"] = endRfc, ["endUtcOffset"] = offset,
                },
                ["amountConsumed"] = new Dictionary<string, object?>
                {
                    ["milliliters"] = milliliters,
                    ["userProvidedUnit"] = "MILLILITER",
                },
            },
        };

        try
        {
            var res = await client.CreateDataPointAsync("hydration-log", body, ct).ConfigureAwait(false);
            if (res.Success)
            {
                string? newId = null;
                if (res.Json is { ValueKind: JsonValueKind.Object } j)
                {
                    if (j.TryGetProperty("response", out var resp) && resp.ValueKind == JsonValueKind.Object
                        && resp.TryGetProperty("name", out var rn))
                        newId = rn.GetString();
                    else if (j.TryGetProperty("name", out var n))
                        newId = n.GetString();
                }
                return JsonSerializer.Serialize(new
                {
                    status = newId is null ? "ok_unverified" : "ok",
                    id = newId,
                    dataType = "hydration-log",
                    written = new { milliliters, time = startRfc },
                }, _json);
            }
            var status = res.StatusCode switch
            {
                401 or 403 => "unauthorized",
                404 => "not_supported",
                _ => "unreachable",
            };
            return Err(status, $"Google Health API {res.StatusCode}: {res.Body}", "hydration-log");
        }
        catch (Exception ex)
        {
            var status = ex.Message.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase)
                         || ex.Message.Contains("401") || ex.Message.Contains("403")
                ? "unauthorized" : "unreachable";
            return Err(status, ex.Message, "hydration-log");
        }
    }

    /// <summary>Shape an EnergyQuantity/WeightQuantity: <c>{ &lt;valueKey&gt;: value [, userProvidedUnit] }</c>.</summary>
    private static Dictionary<string, object?> Quantity(string valueKey, double value, string unitEnum)
    {
        var q = new Dictionary<string, object?> { [valueKey] = value };
        if (!string.IsNullOrEmpty(unitEnum)) q["userProvidedUnit"] = unitEnum;
        return q;
    }

    /// <summary>Recompute the dedup content signature from an existing nutritionLog record.</summary>
    private static string SignatureOf(JsonElement nl, HealthOptions opt)
    {
        var meal = nl.TryGetProperty("mealType", out var m) ? m.GetString() ?? "" : "";
        double cal = 0;
        if (nl.TryGetProperty("energy", out var e) && e.ValueKind == JsonValueKind.Object
            && e.TryGetProperty(opt.EnergyValueKey, out var kv) && kv.ValueKind == JsonValueKind.Number)
            cal = kv.GetDouble();
        var display = nl.TryGetProperty("foodDisplayName", out var fd) ? fd.GetString() ?? "" : "";
        var date = "";
        if (nl.TryGetProperty("interval", out var iv) && iv.ValueKind == JsonValueKind.Object
            && iv.TryGetProperty("startTime", out var st) && st.GetString() is { } sts
            && DateTimeOffset.TryParse(sts, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d))
            date = d.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return Sha256Hex($"{date}|{meal}|{cal:0.####}|{display.ToLowerInvariant()}");
    }

    private static string Sha256Hex(string s)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private static string Err(string status, string note, string? dataType = null)
        => JsonSerializer.Serialize(new { status, dataType, note }, _json);

    private static async Task<string> ListAsync(
        GoogleHealthClient client, string dataType, string start, string end, CancellationToken ct,
        string source = "", int? pageSize = null, string pageToken = "")
    {
        try
        {
            var result = await client.ListDataPointsAsync(
                dataType,
                string.IsNullOrWhiteSpace(start) ? null : start,
                string.IsNullOrWhiteSpace(end) ? null : end,
                pageSize,
                string.IsNullOrWhiteSpace(pageToken) ? null : pageToken,
                ct).ConfigureAwait(false);

            // list has no dataSourceFamily parameter (only reconcile/rollUp do), so source
            // selection happens here on the returned points' own dataSource provenance.
            var points = ExtractPoints(result);
            var filtered = string.IsNullOrWhiteSpace(source)
                ? points
                : points.Where(p => MatchesSource(p, source)).ToList();

            return JsonSerializer.Serialize(new
            {
                dataType,
                count = filtered.Count,
                count_before_source_filter = string.IsNullOrWhiteSpace(source) ? (int?)null : points.Count,
                source = string.IsNullOrWhiteSpace(source) ? null : source,
                nextPageToken = TryGetToken(result),
                dataPoints = filtered,
            }, _json);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                dataType,
                error = ex.Message,
            }, _json);
        }
    }

    /// <summary>The response nests the array one level down as <c>{dataPoints:{dataPoints:[…]}}</c>.</summary>
    private static List<JsonElement> ExtractPoints(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("dataPoints", out var inner)
            && inner.ValueKind == JsonValueKind.Array)
            return inner.EnumerateArray().ToList();
        return [];
    }

    private static string? TryGetToken(JsonElement result)
        => result.ValueKind == JsonValueKind.Object
           && result.TryGetProperty("nextPageToken", out var t)
           && t.ValueKind == JsonValueKind.String
            ? t.GetString()
            : null;

    /// <summary>
    /// Match a point against a source selector. Accepts a <c>formFactor</c> (<c>watch</c>,
    /// <c>phone</c>, …), a <c>platform</c> (<c>HEALTH_CONNECT</c>), or any substring of the
    /// writing app's package name (<c>garmin</c> matches
    /// <c>com.garmin.android.apps.connectmobile</c>).
    ///
    /// <para>This exists because a phone and a watch commonly record the SAME activity — on
    /// this account 45 of 50 step records came from a phone and 5 from a watch over the same
    /// window — so summing unfiltered points double-counts.</para>
    /// </summary>
    private static bool MatchesSource(JsonElement point, string source)
    {
        if (point.ValueKind != JsonValueKind.Object
            || !point.TryGetProperty("dataSource", out var ds)
            || ds.ValueKind != JsonValueKind.Object)
            return false;

        var want = source.Trim();

        if (ds.TryGetProperty("device", out var dev)
            && dev.ValueKind == JsonValueKind.Object
            && dev.TryGetProperty("formFactor", out var ff)
            && ff.ValueKind == JsonValueKind.String
            && string.Equals(ff.GetString(), want, StringComparison.OrdinalIgnoreCase))
            return true;

        if (ds.TryGetProperty("platform", out var pf)
            && pf.ValueKind == JsonValueKind.String
            && string.Equals(pf.GetString(), want, StringComparison.OrdinalIgnoreCase))
            return true;

        if (ds.TryGetProperty("application", out var app)
            && app.ValueKind == JsonValueKind.Object
            && app.TryGetProperty("packageName", out var pkg)
            && pkg.ValueKind == JsonValueKind.String
            && pkg.GetString() is { } name
            && name.Contains(want, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }
}
