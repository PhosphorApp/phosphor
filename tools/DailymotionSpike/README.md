# Dailymotion spike (discovery / auth probe)

Throwaway/experimental harness. Confirms the **one unknown** that decides how easy a Dailymotion
source plug-in is: **does the public API allow discovery (search / categories / channel videos)
WITHOUT OAuth or an API key?**

yt-dlp already proves **playback** — it ships dedicated `dailymotion`, `dailymotion:playlist`,
`dailymotion:search`, and `dailymotion:user` extractors (verified against the bundled yt-dlp
2026.07.04). This spike only probes **discovery**.

## Why it matters

If these calls succeed unauthenticated, Dailymotion is **lower-friction than Vimeo** (which required a
per-user access token): a `DailymotionClient` would need **no credential setup at all** for
search/categories/paging, and playback stays on the existing `YtDlpResolver`.

## Run it

No credentials, nothing to configure:
```powershell
dotnet run
dotnet run -- rock concert   # optional search term
```

## What it checks (`api.dailymotion.com`, unauthenticated)

1. `GET /videos?search=<q>` — public video search.
2. `GET /channels` — Dailymotion's editorial categories/channels (id + name).
3. `GET /channel/{id}/videos` — videos within a category (e.g. `music`).
4. `GET /videos?search=…&page=1` — paging shape (`has_more` / `total`) for `IPagedBrowsable`.

Prints a `SUCCESS` (all keyless) or `PARTIAL` (some need a key/OAuth) conclusion, plus sample rows and
the field names a future `DailymotionClient` would map onto our browse tree.

## Result

See `SOURCE_PLUGIN_CANDIDATES.md` → Dailymotion for the write-up and the spike's findings.
