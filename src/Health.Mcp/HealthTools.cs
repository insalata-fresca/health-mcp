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

    [McpServerTool(Name = "list_weight")]
    [Description(
        "List body-weight data points from Google Health (Withings + Garmin + phone, aggregated via " +
        "Health Connect). Read-only. Optional ISO-8601 start/end bound the window when supplied. " +
        "Verified data type.")]
    public static Task<string> ListWeight(
        GoogleHealthClient client,
        [Description("Optional ISO-8601 window start (e.g. 2026-07-01T00:00:00Z). Empty = no start bound.")] string start = "",
        [Description("Optional ISO-8601 window end. Empty = no end bound.")] string end = "",
        CancellationToken ct = default)
        => ListAsync(client, "weight", start, end, ct);

    [McpServerTool(Name = "list_sleep")]
    [Description(
        "List sleep data points from Google Health (Garmin + phone). Read-only. Optional ISO-8601 " +
        "start/end bound the window. Verified data type.")]
    public static Task<string> ListSleep(
        GoogleHealthClient client,
        [Description("Optional ISO-8601 window start. Empty = no start bound.")] string start = "",
        [Description("Optional ISO-8601 window end. Empty = no end bound.")] string end = "",
        CancellationToken ct = default)
        => ListAsync(client, "sleep", start, end, ct);

    [McpServerTool(Name = "list_steps")]
    [Description(
        "List step-count data points from Google Health (Garmin + phone). Read-only. Optional ISO-8601 " +
        "start/end bound the window. Verified data type.")]
    public static Task<string> ListSteps(
        GoogleHealthClient client,
        [Description("Optional ISO-8601 window start. Empty = no start bound.")] string start = "",
        [Description("Optional ISO-8601 window end. Empty = no end bound.")] string end = "",
        CancellationToken ct = default)
        => ListAsync(client, "steps", start, end, ct);

    [McpServerTool(Name = "list_datapoints")]
    [Description(
        "Generic read of Google Health API v4 data points for ANY data type string. Read-only. " +
        "dataType examples: weight, sleep, steps (verified), heart_rate / activity types (coverage " +
        "to be confirmed against the live account). Optional ISO-8601 start/end bound the window when " +
        "supplied. Use the typed tools (health_list_weight/sleep/steps) for the common cases.")]
    public static Task<string> ListDatapoints(
        GoogleHealthClient client,
        [Description("Google Health data type, e.g. weight, sleep, steps, heart_rate.")] string dataType,
        [Description("Optional ISO-8601 window start. Empty = no start bound.")] string start = "",
        [Description("Optional ISO-8601 window end. Empty = no end bound.")] string end = "",
        CancellationToken ct = default)
        => ListAsync(client, dataType, start, end, ct);

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

        // Timestamp → SessionTimeInterval (start==end; UtcOffset as a google-duration in seconds).
        DateTimeOffset ts;
        if (string.IsNullOrWhiteSpace(time))
            ts = DateTimeOffset.UtcNow;
        else if (!DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture,
                     DateTimeStyles.RoundtripKind, out ts))
            return Err("not_supported", $"Could not parse time '{time}' as ISO-8601.");

        var rfc3339 = ts.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
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
            var existing = await client.ListDataPointsAsync(opt.NutritionDataType, null, null, ct)
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
                ["endTime"] = rfc3339,
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
                string? newId = res.Json is { ValueKind: JsonValueKind.Object } j
                    && j.TryGetProperty("name", out var n) ? n.GetString() : null;
                return JsonSerializer.Serialize(new
                {
                    status = "ok",
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
        GoogleHealthClient client, string dataType, string start, string end, CancellationToken ct)
    {
        try
        {
            var result = await client.ListDataPointsAsync(
                dataType,
                string.IsNullOrWhiteSpace(start) ? null : start,
                string.IsNullOrWhiteSpace(end) ? null : end,
                ct).ConfigureAwait(false);

            var count = result.ValueKind == JsonValueKind.Object
                        && result.TryGetProperty("dataPoints", out var dp)
                        && dp.ValueKind == JsonValueKind.Array
                ? dp.GetArrayLength()
                : (int?)null;

            return JsonSerializer.Serialize(new
            {
                dataType,
                count,
                dataPoints = result,
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
}
