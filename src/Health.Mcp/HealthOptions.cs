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

    public static HealthOptions FromEnvironment()
    {
        static string Req(string k) =>
            Environment.GetEnvironmentVariable(k)
            ?? throw new InvalidOperationException(
                $"{k} is required — set it in the environment (see config.env.example) before start.");
        static string Opt(string k, string dflt) =>
            Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : dflt;

        var dataTypes = Opt("GOOGLE_HEALTH_DATA_TYPES", "weight,sleep,steps,heart_rate")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new HealthOptions
        {
            ClientId = Req("GOOGLE_HEALTH_CLIENT_ID"),
            ClientSecret = Req("GOOGLE_HEALTH_CLIENT_SECRET"),
            RefreshToken = Req("GOOGLE_HEALTH_REFRESH_TOKEN"),
            TokenEndpoint = Opt("GOOGLE_HEALTH_TOKEN_ENDPOINT", "https://oauth2.googleapis.com/token"),
            HealthApiBase = Opt("GOOGLE_HEALTH_API_BASE", "https://health.googleapis.com/v4"),
            DataTypes = dataTypes.Length > 0 ? dataTypes : ["weight", "sleep", "steps", "heart_rate"],
            StartParam = Opt("GOOGLE_HEALTH_START_PARAM", "startTime"),
            EndParam = Opt("GOOGLE_HEALTH_END_PARAM", "endTime"),
        };
    }
}
