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
| `log_nutrition(calories, mealType, name?, time?, protein_g?, carbs_g?, fat_g?, dedupeKey?)` | **WRITE** — log a food entry to `nutrition-log` |

`weight` / `sleep` / `steps` are verified as returning data. `heart_rate` and other
activity/fitness types are advertised for coverage but their return is best-effort until
confirmed against a live account. The data-type set is env-configurable
(`GOOGLE_HEALTH_DATA_TYPES`) — never hardcode-only.

## Nutrition writes (`log_nutrition`)

`log_nutrition` is the **only write tool**. It creates a `nutrition-log` data point via
`POST {base}/users/me/dataTypes/nutrition-log/dataPoints` (scope
`googlehealth.nutrition.writeonly`). All read tools and read scopes are unchanged.

**Re-consent required.** The offline refresh token must be re-consented to add the write
scope `https://www.googleapis.com/auth/googlehealth.nutrition.writeonly` (re-run the
[operator bootstrap](#operator-bootstrap--obtaining-the-refresh-token-one-time) consent
URL with that scope appended). The read tools keep working with the read-only token; only
`log_nutrition` needs the write scope.

**Request body** (an anonymous-food nutrition log — verified shape, see below):

```json
{ "nutritionLog": {
    "interval": { "startTime": "2026-07-16T12:30:00Z", "startUtcOffset": "0s",
                  "endTime":   "2026-07-16T12:30:00Z", "endUtcOffset":   "0s" },
    "mealType": "LUNCH",
    "foodDisplayName": "Chicken salad",
    "energy": { "kcal": 520 },
    "totalCarbohydrate": { "grams": 30 },
    "totalFat": { "grams": 18 },
    "nutrients": [ { "nutrient": "PROTEIN", "quantity": { "grams": 40 } } ]
} }
```

**Idempotency.** Before writing, the tool GETs that day's `nutrition-log` points and compares
a content signature = `SHA-256(date | mealType | calories | name)` against each existing
record; an equivalent one is not re-written (`{status:"duplicate", id}`). The optional
`dedupeKey` is an advisory token echoed back as `derivedKey` — server-side dedup is
content-based because the API has no user-supplied key field.

**Return envelope.** Success `{status:"ok", id, dataType:"nutrition-log", derivedKey, written}`;
otherwise `{status:"duplicate"|"disabled"|"not_configured"|"unauthorized"|"unreachable"|"not_supported", note}`.

### Verified vs unconfirmed (write path)

The nutrition-log envelope was **verified against the Google Health API v4 discovery
document** (`https://health.googleapis.com/$discovery/rest?version=v4`, fetched 2026-07-16):

- DataPoint wrapper key `nutritionLog`; NutritionLog fields `interval`, `mealType`,
  `foodDisplayName`, `energy`, `totalCarbohydrate`, `totalFat`, `nutrients` — **verified**.
- `EnergyQuantity` = `{ "kcal": <number> }`, `WeightQuantity` = `{ "grams": <number> }`
  (raw value fields — **not** a `{unit, value}` pair) — **verified**.
- `mealType` enum `BREAKFAST` / `LUNCH` / `DINNER` / `SNACK` (also `BEFORE_*`, `AFTER_DINNER`,
  `ANYTIME`) — **verified**.
- `NutrientQuantity` = `{ "nutrient": <enum>, "quantity": { "grams": <number> } }` with
  `PROTEIN` a member of the Nutrient enum — **verified** (protein is a `nutrients[]` entry,
  **not** a top-level NutritionLog field).
- **Unconfirmed:** no successful live write (HTTP 200) has been performed, so the end-to-end
  round trip is not yet proven. Every shape/enum above is therefore kept **env-overridable**
  (`GOOGLE_HEALTH_NUTRITION_WRAPPER_KEY`, `_DATATYPE`, `_ENERGY_VALUE_KEY`, `_MASS_VALUE_KEY`,
  `_ENERGY_UNIT`, `_MASS_UNIT`, `_PROTEIN_NUTRIENT`, `_MEALTYPE_MAP`) so a first-write 400 is a
  config fix, not a rebuild. A write kill-switch is `GOOGLE_HEALTH_NUTRITION_WRITE_ENABLED`.

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
| `GOOGLE_HEALTH_NUTRITION_WRITE_ENABLED` | no | write kill-switch (default true) |
| `GOOGLE_HEALTH_NUTRITION_DATATYPE` / `_WRAPPER_KEY` | no | write dataType + DataPoint key (verified) |
| `GOOGLE_HEALTH_ENERGY_VALUE_KEY` / `_MASS_VALUE_KEY` | no | quantity value fields `kcal` / `grams` (verified) |
| `GOOGLE_HEALTH_ENERGY_UNIT` / `_MASS_UNIT` | no | optional `userProvidedUnit` enums (empty=omit) |
| `GOOGLE_HEALTH_PROTEIN_NUTRIENT` | no | Nutrient enum for protein (verified `PROTEIN`) |
| `GOOGLE_HEALTH_MEALTYPE_MAP` | no | meal arg → MealType enum map (verified) |
| `HEALTH_MCP_PORT` | no | listen port (default 9226) |

## Operator bootstrap — obtaining the refresh token (one-time)

This needs a human browser consent, so it is out of scope for the service code — do it once,
then the MCP refreshes access tokens itself forever after.

1. **Create a Google OAuth Desktop client** in Google Cloud Console and enable the Google
   Health API. Grant it the three read-only scopes below. **To use `log_nutrition`, also add
   the write scope** `https://www.googleapis.com/auth/googlehealth.nutrition.writeonly` to the
   consent URL's `scope=` list (space-separated) and re-consent.

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
