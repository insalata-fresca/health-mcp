namespace Health.Mcp;

/// <summary>
/// Runtime configuration for the Google Health API v4 read-only MCP, resolved
/// from environment variables (see <c>config.env.example</c>). Non-secret values
/// have sane defaults; the secrets must be supplied before start.
///
/// <para>The three credential fields are the ONLY secrets. <c>ClientId</c> is not
/// secret (a Google Desktop OAuth client id); <c>ClientSecret</c> + <c>RefreshToken</c>
/// are. None of them are ever logged.</para>
/// </summary>
public sealed record HealthOptions
{
    // ── OAuth2 (a Google Desktop OAuth client; access_type=offline) ──
    /// <summary>OAuth2 client id (Google Desktop OAuth client — NOT secret).</summary>
    public required string ClientId { get; init; }
    /// <summary>OAuth2 client secret (SECRET).</summary>
    public required string ClientSecret { get; init; }
    /// <summary>Long-lived offline refresh token (SECRET — operator-bootstrapped).</summary>
    public required string RefreshToken { get; init; }

    /// <summary>Google OAuth2 token endpoint (refresh_token grant).</summary>
    public string TokenEndpoint { get; init; } = "https://oauth2.googleapis.com/token";

    /// <summary>Google Health API v4 base (dataPoints are read under this).</summary>
    public string HealthApiBase { get; init; } = "https://health.googleapis.com/v4";

    /// <summary>
    /// The data types the convenience/aggregate surfaces advertise. <c>weight</c>,
    /// <c>sleep</c>, <c>steps</c> were verified live returning data. <c>heart_rate</c>
    /// is included for coverage but its return is to be CONFIRMED by the running service — the
    /// set is env-configurable so it is never hardcode-only. The generic
    /// <c>health_list_datapoints</c> tool accepts ANY data type string regardless.
    /// </summary>
    public string[] DataTypes { get; init; } = ["weight", "sleep", "steps", "heart_rate"];

    /// <summary>
    /// Query-parameter name forwarded for a caller-supplied window START, when the
    /// caller passes one. UNVERIFIED against the live v4 API (the initial probe proved
    /// only the param-less list) — env-overridable so the running service can be tuned to
    /// the real param name without a rebuild. Empty ⇒ never forward a start param.
    /// </summary>
    public string StartParam { get; init; } = "startTime";

    /// <summary>Query-parameter name for a caller-supplied window END. Same UNVERIFIED
    /// caveat as <see cref="StartParam"/>. Empty ⇒ never forward an end param.</summary>
    public string EndParam { get; init; } = "endTime";

    // ── Nutrition WRITE (log_nutrition) config ──────────────────────────────
    // Every shape/enum below was VERIFIED against the Google Health API v4 discovery
    // document (https://health.googleapis.com/$discovery/rest?version=v4, fetched
    // 2026-07-16) — DataPoint.nutritionLog → NutritionLog{ interval(SessionTimeInterval),
    // foodDisplayName, mealType(enum), energy(EnergyQuantity{kcal}),
    // totalCarbohydrate/totalFat(WeightQuantity{grams}), nutrients[](NutrientQuantity
    // {nutrient(enum), quantity(WeightQuantity{grams})}) }. They are STILL kept
    // env-overridable so a first-live-write 400 (which has never been observed as a
    // successful 200 write — no live write was performed) is a config fix, not a rebuild.

    /// <summary>Kill-switch for the write tool. When false, <c>log_nutrition</c> returns
    /// <c>{status:"disabled"}</c> without touching the upstream API. Env
    /// <c>GOOGLE_HEALTH_NUTRITION_WRITE_ENABLED</c> (default true).</summary>
    public bool NutritionWriteEnabled { get; init; } = true;

    /// <summary>The dataType path segment for nutrition writes/reads
    /// (<c>.../dataTypes/{dataType}/dataPoints</c>). VERIFIED = <c>nutrition-log</c>.
    /// Env <c>GOOGLE_HEALTH_NUTRITION_DATATYPE</c>.</summary>
    public string NutritionDataType { get; init; } = "nutrition-log";

    /// <summary>The DataPoint key that wraps the nutrition record. VERIFIED = <c>nutritionLog</c>
    /// (discovery doc DataPoint.nutritionLog). Env <c>GOOGLE_HEALTH_NUTRITION_WRAPPER_KEY</c>.</summary>
    public string NutritionWrapperKey { get; init; } = "nutritionLog";

    /// <summary>Field name carrying the energy value inside EnergyQuantity. VERIFIED = <c>kcal</c>
    /// (a raw number, NOT a {unit,value} pair). Env <c>GOOGLE_HEALTH_ENERGY_VALUE_KEY</c>.</summary>
    public string EnergyValueKey { get; init; } = "kcal";

    /// <summary>Field name carrying the mass value inside WeightQuantity. VERIFIED = <c>grams</c>
    /// (a raw number). Env <c>GOOGLE_HEALTH_MASS_VALUE_KEY</c>.</summary>
    public string MassValueKey { get; init; } = "grams";

    /// <summary>Optional EnergyQuantity <c>userProvidedUnit</c> enum to record alongside kcal
    /// (verified enum incl. <c>KILOCALORIE</c>). Empty ⇒ omit (kcal is already the canonical
    /// value). Env <c>GOOGLE_HEALTH_ENERGY_UNIT</c>.</summary>
    public string EnergyUnitEnum { get; init; } = "";

    /// <summary>Optional WeightQuantity <c>userProvidedUnit</c> enum to record alongside grams
    /// (verified enum incl. <c>GRAM</c>). Empty ⇒ omit. Env <c>GOOGLE_HEALTH_MASS_UNIT</c>.</summary>
    public string MassUnitEnum { get; init; } = "";

    /// <summary>The Nutrient enum value used for the protein entry. VERIFIED = <c>PROTEIN</c>
    /// (discovery doc Nutrient enum). Env <c>GOOGLE_HEALTH_PROTEIN_NUTRIENT</c>.</summary>
    public string ProteinNutrient { get; init; } = "PROTEIN";

    /// <summary>Maps the tool's lower-case meal argument → the Google Health MealType enum.
    /// VERIFIED enum values (discovery doc): BREAKFAST/LUNCH/DINNER/SNACK (also BEFORE_*,
    /// AFTER_DINNER, ANYTIME). Env <c>GOOGLE_HEALTH_MEALTYPE_MAP</c> as
    /// <c>breakfast=BREAKFAST,lunch=LUNCH,dinner=DINNER,snack=SNACK</c>.</summary>
    public IReadOnlyDictionary<string, string> MealTypeMap { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["breakfast"] = "BREAKFAST",
            ["lunch"] = "LUNCH",
            ["dinner"] = "DINNER",
            ["snack"] = "SNACK",
        };

    public static HealthOptions FromEnvironment()
    {
        static string Req(string k) =>
            Environment.GetEnvironmentVariable(k)
            ?? throw new InvalidOperationException(
                $"{k} is required — set it in the environment (see config.env.example) before start.");
        static string Opt(string k, string dflt) =>
            Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : dflt;

        static bool Flag(string k, bool dflt) =>
            Environment.GetEnvironmentVariable(k) is { Length: > 0 } v
                ? !(v.Equals("false", StringComparison.OrdinalIgnoreCase) || v == "0")
                : dflt;

        var dataTypes = Opt("GOOGLE_HEALTH_DATA_TYPES", "weight,sleep,steps,heart_rate,nutrition-log")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var mealMap = ParseMap(
            Opt("GOOGLE_HEALTH_MEALTYPE_MAP", "breakfast=BREAKFAST,lunch=LUNCH,dinner=DINNER,snack=SNACK"));

        return new HealthOptions
        {
            ClientId = Req("GOOGLE_HEALTH_CLIENT_ID"),
            ClientSecret = Req("GOOGLE_HEALTH_CLIENT_SECRET"),
            RefreshToken = Req("GOOGLE_HEALTH_REFRESH_TOKEN"),
            TokenEndpoint = Opt("GOOGLE_HEALTH_TOKEN_ENDPOINT", "https://oauth2.googleapis.com/token"),
            HealthApiBase = Opt("GOOGLE_HEALTH_API_BASE", "https://health.googleapis.com/v4"),
            DataTypes = dataTypes.Length > 0
                ? dataTypes
                : ["weight", "sleep", "steps", "heart_rate", "nutrition-log"],
            StartParam = Opt("GOOGLE_HEALTH_START_PARAM", "startTime"),
            EndParam = Opt("GOOGLE_HEALTH_END_PARAM", "endTime"),
            NutritionWriteEnabled = Flag("GOOGLE_HEALTH_NUTRITION_WRITE_ENABLED", true),
            NutritionDataType = Opt("GOOGLE_HEALTH_NUTRITION_DATATYPE", "nutrition-log"),
            NutritionWrapperKey = Opt("GOOGLE_HEALTH_NUTRITION_WRAPPER_KEY", "nutritionLog"),
            EnergyValueKey = Opt("GOOGLE_HEALTH_ENERGY_VALUE_KEY", "kcal"),
            MassValueKey = Opt("GOOGLE_HEALTH_MASS_VALUE_KEY", "grams"),
            EnergyUnitEnum = Opt("GOOGLE_HEALTH_ENERGY_UNIT", ""),
            MassUnitEnum = Opt("GOOGLE_HEALTH_MASS_UNIT", ""),
            ProteinNutrient = Opt("GOOGLE_HEALTH_PROTEIN_NUTRIENT", "PROTEIN"),
            MealTypeMap = mealMap.Count > 0 ? mealMap : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["breakfast"] = "BREAKFAST",
                ["lunch"] = "LUNCH",
                ["dinner"] = "DINNER",
                ["snack"] = "SNACK",
            },
        };
    }

    /// <summary>Parse a <c>k1=v1,k2=v2</c> env string into a case-insensitive map.</summary>
    private static Dictionary<string, string> ParseMap(string raw)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var i = pair.IndexOf('=');
            if (i <= 0 || i >= pair.Length - 1) continue;
            map[pair[..i].Trim()] = pair[(i + 1)..].Trim();
        }
        return map;
    }
}
