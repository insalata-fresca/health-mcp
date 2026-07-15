using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Health.Mcp;

/// <summary>
/// Read-only client for the Google Health API v4. Owns the OAuth2 access-token
/// lifecycle: it holds a long-lived offline <c>refresh_token</c> and mints /
/// caches short-lived access tokens itself (<c>grant_type=refresh_token</c>
/// against the Google token endpoint), refreshing ~1 minute before expiry.
///
/// <para>Registered as a typed <see cref="HttpClient"/> via
/// <c>IHttpClientFactory</c> (<c>AddHttpClient&lt;GoogleHealthClient&gt;</c>) so
/// socket/handler lifetime is pooled correctly. A single shared instance is safe
/// for concurrent tool calls: the token cache is guarded by a
/// <see cref="SemaphoreSlim"/>.</para>
///
/// <para>The proven read surface is
/// <c>GET {base}/users/me/dataTypes/{dataType}/dataPoints</c> with
/// <c>Authorization: Bearer</c> + <c>Accept: application/json</c>. Optional
/// caller-supplied time-window params are forwarded only when non-empty and only
/// when their (env-configurable, currently UNVERIFIED) param names are set.</para>
/// </summary>
public sealed class GoogleHealthClient(HttpClient http, HealthOptions opt, ILogger<GoogleHealthClient> log)
{
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiry = DateTimeOffset.MinValue;

    /// <summary>
    /// List raw data points for a single Google Health data type. Returns the API
    /// response body as a parsed <see cref="JsonElement"/> (the tool layer decides
    /// how to shape it). Throws on a non-success HTTP status with the status + body
    /// so the caller surfaces a meaningful error.
    /// </summary>
    public async Task<JsonElement> ListDataPointsAsync(
        string dataType, string? start, string? end, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dataType))
            throw new ArgumentException("dataType is required (e.g. weight, sleep, steps).", nameof(dataType));

        var token = await GetAccessTokenAsync(ct).ConfigureAwait(false);

        var url = $"{opt.HealthApiBase.TrimEnd('/')}/users/me/dataTypes/{Uri.EscapeDataString(dataType)}/dataPoints";
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(start) && !string.IsNullOrEmpty(opt.StartParam))
            query.Add($"{opt.StartParam}={Uri.EscapeDataString(start)}");
        if (!string.IsNullOrWhiteSpace(end) && !string.IsNullOrEmpty(opt.EndParam))
            query.Add($"{opt.EndParam}={Uri.EscapeDataString(end)}");
        if (query.Count > 0)
            url += "?" + string.Join("&", query);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Google Health API {(int)resp.StatusCode} for dataType '{dataType}': {Truncate(body)}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    /// <summary>Return a valid cached access token, refreshing it if missing/expired.</summary>
    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        if (_accessToken is not null && DateTimeOffset.UtcNow < _accessTokenExpiry)
            return _accessToken;

        await _tokenLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_accessToken is not null && DateTimeOffset.UtcNow < _accessTokenExpiry)
                return _accessToken;

            using var req = new HttpRequestMessage(HttpMethod.Post, opt.TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = opt.ClientId,
                    ["client_secret"] = opt.ClientSecret,
                    ["refresh_token"] = opt.RefreshToken,
                    ["grant_type"] = "refresh_token",
                }),
            };

            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                // Body may name the OAuth error (invalid_grant ⇒ the refresh token was
                // revoked/expired — re-run the operator bootstrap). Never logs the token.
                throw new HttpRequestException(
                    $"OAuth token refresh failed ({(int)resp.StatusCode}): {Truncate(body)}");

            var tok = JsonSerializer.Deserialize<TokenResponse>(body)
                ?? throw new InvalidOperationException("Token endpoint returned an unparseable body.");
            if (string.IsNullOrEmpty(tok.access_token))
                throw new InvalidOperationException("Token endpoint returned no access_token.");

            _accessToken = tok.access_token;
            // Refresh a minute early to avoid a race with an in-flight call.
            var lifetime = tok.expires_in > 60 ? tok.expires_in - 60 : tok.expires_in;
            _accessTokenExpiry = DateTimeOffset.UtcNow.AddSeconds(lifetime);
            log.LogInformation("Refreshed Google Health access token (valid ~{Seconds}s).", tok.expires_in);
            return _accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private static string Truncate(string s) => s.Length <= 500 ? s : s[..500];

    private sealed record TokenResponse(string? access_token, int expires_in, string? token_type, string? scope);
}
