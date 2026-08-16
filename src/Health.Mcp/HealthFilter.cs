namespace Health.Mcp;

/// <summary>
/// Builds the AIP-160 <c>filter</c> expression that bounds a time window on
/// <c>users.dataTypes.dataPoints.list</c>.
///
/// <para><b>Why this class exists.</b> The first implementation forwarded the window as
/// <c>?startTime=…&amp;endTime=…</c>. Those parameters do not exist: the v4 discovery
/// document (revision 20260805) shows <c>list</c> accepting exactly <c>parent</c>,
/// <c>filter</c>, <c>pageSize</c> and <c>pageToken</c>, so EVERY windowed call returned
/// <c>400 INVALID_ARGUMENT — Cannot bind query parameter</c>, for every data type
/// including the "verified" ones. The window has to be expressed as a filter string, and
/// the field it filters on depends on the data type's KIND — which is why swapping the
/// parameter NAME (the knob the old code exposed) could never have fixed it.</para>
///
/// <para><b>Two spellings, one request.</b> The path segment is kebab-case
/// (<c>daily-resting-heart-rate</c>) while the filter field is snake_case
/// (<c>daily_resting_heart_rate</c>). Getting this wrong is a 400.</para>
///
/// <para><b>Grammar limits</b> (from the API's own <c>filter</c> description): only
/// <c>&gt;=</c> and <c>&lt;</c> are accepted — never <c>&gt;</c>, <c>&lt;=</c> or
/// <c>=</c> — and only <c>AND</c> joins terms. Values are quoted string literals.
/// Results come back ordered by interval start time DESCENDING.</para>
/// </summary>
public static class HealthFilter
{
    /// <summary>
    /// How a data type expresses its time coordinate, which decides the filter field.
    /// </summary>
    public enum Kind
    {
        /// <summary>Has an interval; filter on <c>{type}.interval.start_time</c>. The default.</summary>
        Interval,

        /// <summary>A point-in-time sample; filter on <c>{type}.sample_time.physical_time</c>.</summary>
        Sample,

        /// <summary>A <c>daily-*</c> summary keyed by calendar date; filter on <c>{type}.date</c>.</summary>
        DailySummary,

        /// <summary>Sleep sessions are bounded on their END; filter on <c>sleep.interval.end_time</c>.</summary>
        Sleep,
    }

    /// <summary>
    /// Data types whose time coordinate is a <c>sample_time</c> rather than an interval.
    /// VERIFIED for <c>weight</c> and <c>height</c> (the two the API reference names as
    /// sample-kind). The rest are classified by the documented default (Interval).
    /// Override via <c>GOOGLE_HEALTH_SAMPLE_TYPES</c> when a 400 shows one is mis-binned —
    /// the error message names the field that was attempted, so the correction is obvious.
    /// </summary>
    public static readonly string[] DefaultSampleTypes = ["weight", "height", "heart-rate"];

    /// <summary>Classify a data type. <paramref name="sampleTypes"/> overrides the sample set.</summary>
    public static Kind ClassifyKind(string dataType, IEnumerable<string>? sampleTypes = null)
    {
        if (string.IsNullOrWhiteSpace(dataType))
            throw new ArgumentException("dataType is required.", nameof(dataType));

        var t = dataType.Trim();

        // sleep is a session bounded on its end time — checked before the generic interval
        // default because it is the one documented exception among session types.
        if (t.Equals("sleep", StringComparison.OrdinalIgnoreCase))
            return Kind.Sleep;

        // Every daily roll-up type is keyed by a calendar date and has no interval at all.
        if (t.StartsWith("daily-", StringComparison.OrdinalIgnoreCase) ||
            t.StartsWith("daily_", StringComparison.OrdinalIgnoreCase))
            return Kind.DailySummary;

        var samples = sampleTypes as string[] ?? sampleTypes?.ToArray() ?? DefaultSampleTypes;
        foreach (var s in samples)
            if (t.Equals(s.Trim(), StringComparison.OrdinalIgnoreCase))
                return Kind.Sample;

        return Kind.Interval;
    }

    /// <summary>
    /// The filter FIELD name for a data type — snake_case, unlike the kebab-case path segment.
    /// </summary>
    public static string FieldFor(string dataType, IEnumerable<string>? sampleTypes = null)
    {
        var snake = ToSnake(dataType);
        return ClassifyKind(dataType, sampleTypes) switch
        {
            Kind.Sleep => $"{snake}.interval.end_time",
            Kind.DailySummary => $"{snake}.date",
            Kind.Sample => $"{snake}.sample_time.physical_time",
            _ => $"{snake}.interval.start_time",
        };
    }

    /// <summary>
    /// Build the filter for a window. Either bound may be empty (open-ended); both empty
    /// returns <c>null</c>, meaning "send no filter at all" — which is the API's own
    /// unbounded behaviour and what the tools did before this fix.
    ///
    /// <para>A DailySummary type takes bare <c>YYYY-MM-DD</c> literals, so an ISO-8601
    /// instant is truncated to its date part rather than rejected: callers pass windows as
    /// timestamps and should not have to know which types are date-keyed.</para>
    /// </summary>
    public static string? BuildWindow(
        string dataType, string? start, string? end, IEnumerable<string>? sampleTypes = null)
    {
        var hasStart = !string.IsNullOrWhiteSpace(start);
        var hasEnd = !string.IsNullOrWhiteSpace(end);
        if (!hasStart && !hasEnd) return null;

        var field = FieldFor(dataType, sampleTypes);
        var isDate = ClassifyKind(dataType, sampleTypes) == Kind.DailySummary;

        var terms = new List<string>(2);
        // Only >= and < exist in this grammar; a closed-open window is also the correct
        // shape for a day boundary, so this is not merely a limitation to work around.
        if (hasStart) terms.Add($"{field} >= \"{Literal(start!, isDate)}\"");
        if (hasEnd) terms.Add($"{field} < \"{Literal(end!, isDate)}\"");
        return string.Join(" AND ", terms);
    }

    /// <summary>kebab-case path segment → snake_case filter field.</summary>
    public static string ToSnake(string dataType)
    {
        if (string.IsNullOrWhiteSpace(dataType))
            throw new ArgumentException("dataType is required.", nameof(dataType));
        return dataType.Trim().Replace('-', '_');
    }

    /// <summary>
    /// Normalise a bound for the grammar.
    ///
    /// <para>The grammar has no escape sequence, so a value carrying a quote could only ever
    /// produce a malformed expression or one that changes meaning. Rather than strip just
    /// the quote — which leaves the rest of an injected fragment sitting inside the literal —
    /// this keeps ONLY the characters an RFC-3339 timestamp or an ISO date can contain.
    /// Anything else is dropped, so a bound cannot contribute grammar (no quote to close the
    /// literal, and no bare <c>AND</c>/<c>OR</c> to be read as an operator even if it did).
    /// A mangled bound then fails upstream as an invalid timestamp — fail-closed, and the
    /// error names the value.</para>
    /// </summary>
    private static string Literal(string raw, bool dateOnly)
    {
        Span<char> buf = stackalloc char[raw.Length];
        var n = 0;
        foreach (var c in raw.Trim())
            if (char.IsAsciiDigit(c) || c is '-' or ':' or 'T' or 'Z' or '+' or '.')
                buf[n++] = c;
        var v = new string(buf[..n]);

        if (dateOnly)
        {
            // "2026-08-16T00:00:00Z" → "2026-08-16"; an already-bare date passes through.
            var t = v.IndexOf('T');
            return t > 0 ? v[..t] : v;
        }

        // The mirror case, and the one that bit a caller: an INTERVAL or SAMPLE type needs a full
        // RFC-3339 instant, so a bare "2026-08-16" is rejected upstream. Callers reasonably expect
        // the same date form to work everywhere — the split between date-keyed and interval-keyed
        // types is our implementation detail, not theirs — so widen a bare date to the start of
        // that day rather than making them remember which types are which.
        return IsBareDate(v) ? v + "T00:00:00Z" : v;
    }

    /// <summary>True for exactly <c>YYYY-MM-DD</c>.</summary>
    private static bool IsBareDate(string v)
    {
        if (v.Length != 10 || v[4] != '-' || v[7] != '-') return false;
        for (var i = 0; i < 10; i++)
            if (i != 4 && i != 7 && !char.IsAsciiDigit(v[i])) return false;
        return true;
    }
}
