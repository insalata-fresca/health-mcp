# Google Health — Nutrition Logging Reference

**Audience:** Claude, working in the personal health project. **Last verified:** 2026-07-16.
**Platform:** Google Health API v4 (`health.googleapis.com`, package `google.devicesandservices.health.v4`) — the Fitbit-successor platform. Consumer app: **Google Health (Fitbit)**, `com.fitbit.FitbitMobile`.

This document is grounded in Google's official docs (quoted + linked at the bottom) and in live round-trip tests against the operator's account. Where something is **not** documented or **not** verified, it says so — do not assume beyond this.

---

## TL;DR — how to log and read a meal

**Log a meal:**
```
health_log_nutrition(
  calories   = <kcal>,                # required
  mealType   = "breakfast|lunch|dinner|snack",   # required
  name       = "Cappuccino + brioche",           # optional but recommended
  time       = "2026-07-16T08:00:00Z",           # optional ISO-8601; empty = now (UTC)
  protein_g  = 12, carbs_g = 60, fat_g = 18,      # all optional
  dedupeKey  = "<optional idempotency token>"
)
```

**Read it back — the data type id is `nutrition-log`, NOT `nutrition`:**
```
health_list_nutrition(start="2026-07-16T00:00:00Z")          # typed, preferred
# or, generic:
health_list_datapoints(dataType="nutrition-log", start=...)  # `nutrition` → INVALID_PARENT_DATA_TYPE_COLLECTION
```

**Response semantics (read them, don't assume `ok`):**
| status | meaning |
|---|---|
| `ok` | persisted upstream, real Google record id returned in `id` |
| `duplicate` | an equivalent entry already exists for that day; not re-written (`id` = existing record) |
| `ok_unverified` | the create returned 2xx but no id could be parsed — treat as "probably written, unconfirmed" |
| `disabled` / `not_configured` / `unauthorized` / `not_supported` / `unreachable` | write did not happen; read the `note` |

**Dedup key:** content signature = `date | mealType | calories | name(lowercased)`. Same signature on the same day → `duplicate`. Pass an explicit `dedupeKey` only to override.

---

## The deployed tools (through the bridge)

All are exposed on the bridge with a `health_` prefix (the gateway strips it → target `health`).

| Tool | Purpose | Notes |
|---|---|---|
| `health_log_nutrition` | Write one meal (nutrition-log DataPoint) | Only **write** tool. See semantics above. |
| `health_list_nutrition` | Read nutrition-log entries | Typed read for `nutrition-log`. |
| `health_list_datapoints` | Generic read of any data type | Use exact id, e.g. `nutrition-log`, `weight`, `sleep`, `steps`. |
| `health_list_data_types` | List advertised data types | Now includes `nutrition-log`. |
| `health_list_weight` / `_sleep` / `_steps` | Typed reads | Aggregated via Health Connect (Garmin/Withings/phone). |

---

## How the underlying API works (documented facts)

- **Write is an intended, documented capability.** Scope `https://www.googleapis.com/auth/googlehealth.nutrition.writeonly` — *"Add nutrition data to Google Health, and edit or delete the data it adds."* Added **2026-05-26**; it is a **Restricted** scope (Google privacy/security review — the operator's consent passed it).
- **Create is asynchronous.** `POST .../users/me/dataTypes/nutrition-log/dataPoints` → *"the response body contains a newly created instance of `Operation`."* The created record id is at **`response.name`** inside that Operation wrapper (e.g. `users/<uid>/dataTypes/nutrition-log/dataPoints/<id>`), **not** top-level — the tool extracts it.
- **Interval must be strictly `start < end`.** A point-in-time (`start == end`) is rejected with `INVALID_TIME_RANGE`. The tool sets `endTime = startTime + 1 min` internally.
- **Identified food vs anonymous food (verbatim):** *"There are two ways of creating a nutrition log… 1. Identified food: Using the `food` field, which is a reference to a Food resource… 2. Anonymous food: Using the `foodDisplayName` field and setting the nutrients… manually. **The identified food is preferred over the anonymous food. Nutrition logs created from anonymous food are not be editable.**"* Related data types `food` and `food-measurement-unit` were added the same date (the catalog to resolve a `food` id).
- **Nutrition-log JSON:** `interval`, `mealType` (BREAKFAST|LUNCH|DINNER|SNACK), `energy` (`kcal`), `totalCarbohydrate`/`totalFat` (`grams`), `nutrients[]` (e.g. `{nutrient: PROTEIN, quantity:{grams}}`), `foodDisplayName`, `food`.

## Verified live (2026-07-16)

- Write **persists to Google** and is **re-readable** via the API: a fresh `list_nutrition` returned the written meals with real Google resource ids (`dataSource.platform: GOOGLE_WEB_API`). health-mcp is stateless — there is **no internal store**; records live on Google.
- Round-trip + dedup work: write → read back → repeat = `duplicate`.

## NOT verified / NOT documented — do not claim otherwise

- **Whether API-written nutrition appears in the Google Health (Fitbit) app UI is undocumented.** Google's docs describe a *"Reconciled Stream"* that merges sources but say nothing about API-write → app rendering, and say **nothing about Health Connect** for the nutrition write path. As of testing, entries were **not** visible in the app. Treat app-UI visibility as **unconfirmed**, not guaranteed.
- Current implementation logs **anonymous food** (the non-preferred, non-editable mode). Moving to **identified food** (resolve a `food` id, then reference it) is the documented-preferred path and a likely improvement — planned, not yet done.

## Ruled out

- **MyFitnessPal API:** partner/private-access only; **no public endpoint to write food diary entries.** The only community options are scraping libraries (require a *public* diary, break on MFP changes, violate ToS). Not used.

## Sources
- Scopes: https://developers.google.com/health/scopes
- DataPoints resource (+ `#nutritionlog`): https://developers.google.com/health/reference/rest/v4/users.dataTypes.dataPoints
- Create method: https://developers.google.com/health/reference/rest/v4/users.dataTypes.dataPoints/create
- Data types: https://developers.google.com/health/data-types
- Release notes: https://developers.google.com/health/release-notes
- About (Reconciled Stream): https://developers.google.com/health/about
- App nutrition sources: https://support.google.com/googlehealth/answer/14237210
