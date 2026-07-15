# health-mcp — Google Health API v4 (read-only) MCP

A small, self-contained C# [Model Context Protocol](https://modelcontextprotocol.io)
server that exposes your aggregated **Google Health** (Health Connect) data — weight,
sleep, steps and more, unified from Garmin + Withings + phone — as **read-only** tools
over streamable HTTP.

Built on `Sinapsi.Mcp` + `ModelContextProtocol.AspNetCore` (.NET 8). Default port **9226**.

## Tools

| Tool | What it returns |
|---|---|
| `list_weight(start?, end?)` | body-weight data points |
| `list_sleep(start?, end?)` | sleep data points |
| `list_steps(start?, end?)` | step-count data points |
| `list_datapoints(dataType, start?, end?)` | generic read for ANY data type string |
| `list_data_types()` | the configured/advertised data types (no upstream call) |

`weight` / `sleep` / `steps` are verified as returning data. `heart_rate` and other
activity/fitness types are advertised for coverage but their return is best-effort until
confirmed against a live account. The data-type set is env-configurable
(`GOOGLE_HEALTH_DATA_TYPES`) — never hardcode-only.

## How it works

- Reads `GET {base}/users/me/dataTypes/{dataType}/dataPoints` with `Authorization: Bearer` +
  `Accept: application/json`.
- Holds a long-lived offline **refresh token** and mints/caches short-lived access tokens
  itself (`grant_type=refresh_token` against `https://oauth2.googleapis.com/token`), refreshing
  ~1 min before expiry. The `GoogleHealthClient` is a pooled typed `HttpClient`
  (`IHttpClientFactory`).

### Unverified surface (flagged, not silently assumed)

The proven read is the **param-less** list. The optional `start`/`end` window is forwarded as
query params whose NAMES (`GOOGLE_HEALTH_START_PARAM` / `GOOGLE_HEALTH_END_PARAM`, default
`startTime`/`endTime`) are **UNVERIFIED** against the live v4 API. They are env-overridable so
the running service can be corrected to the real names — or set either to empty to stop
forwarding that bound — without a rebuild.

## Configuration

All configuration is via environment variables — see [`config.env.example`](config.env.example).

| Var | Secret? | Notes |
|---|---|---|
| `GOOGLE_HEALTH_CLIENT_ID` | no | Google Desktop OAuth client id |
| `GOOGLE_HEALTH_CLIENT_SECRET` | yes | Google OAuth client secret |
| `GOOGLE_HEALTH_REFRESH_TOKEN` | yes | offline refresh token (see bootstrap) |
| `GOOGLE_HEALTH_DATA_TYPES` | no | advertised types, comma-separated |
| `GOOGLE_HEALTH_START_PARAM` / `_END_PARAM` | no | window query-param names (unverified) |
| `HEALTH_MCP_PORT` | no | listen port (default 9226) |

## Operator bootstrap — obtaining the refresh token (one-time)

This needs a human browser consent, so it is out of scope for the service code — do it once,
then the MCP refreshes access tokens itself forever after.

1. **Create a Google OAuth Desktop client** in Google Cloud Console and enable the Google
   Health API. Grant it the three read-only scopes below.

2. **Build the consent URL** and open it in a browser (logged in as your Google account):

   ```
   https://accounts.google.com/o/oauth2/v2/auth
     ?client_id=<YOUR_CLIENT_ID>.apps.googleusercontent.com
     &redirect_uri=http://localhost
     &response_type=code
     &access_type=offline
     &prompt=consent
     &scope=https://www.googleapis.com/auth/googlehealth.health_metrics_and_measurements.readonly%20https://www.googleapis.com/auth/googlehealth.sleep.readonly%20https://www.googleapis.com/auth/googlehealth.activity_and_fitness.readonly
   ```

   (`prompt=consent` + `access_type=offline` is what guarantees a **refresh_token** is issued.)

3. **Copy the `code=` value** from the `http://localhost/?code=...` redirect (the page will fail
   to load — that is expected; only the URL matters).

4. **Exchange the code for tokens:**

   ```bash
   curl -s https://oauth2.googleapis.com/token \
     -d client_id=<YOUR_CLIENT_ID>.apps.googleusercontent.com \
     -d client_secret=<YOUR_CLIENT_SECRET> -d code=<CODE> \
     -d grant_type=authorization_code -d redirect_uri=http://localhost
   ```

   The response `refresh_token` is the value for `GOOGLE_HEALTH_REFRESH_TOKEN`.

## Build & run

```bash
docker build -t health-mcp .
cp config.env.example config.env   # fill in the client id + the two secrets
docker run --rm -p 9226:9226 --env-file config.env health-mcp
```

The MCP endpoint is served at `http://localhost:9226/mcp` (streamable HTTP, stateless).

> This project references `Sinapsi.Mcp` (a small MCP-host helper library). Every other
> dependency resolves from nuget.org; provide a NuGet source that serves `Sinapsi.Mcp`
> (e.g. add a `nuget.config`) if it is not already on your configured feeds.

## License

[MIT](LICENSE) © ste
