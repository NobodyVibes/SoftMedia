# OMDB API Key Setup

This document describes how SoftMedia's OMDb movie-metadata key works — both the
project-funded shared key and the per-user fallback — and how to configure it.

## The shared-key model (P1-WI-004)

SoftMedia ships with a **maintainer-funded shared OMDb key** as the default
(`OMDbApiKeyMode = "softmedia"`). End users get movie metadata with zero setup; the
project covers the OMDb API cost.

This is **not** a cloud dependency. Clients still call `api.omdbapi.com` directly from
their own server — only the API *quota* is funded centrally. No SoftMedia-operated
relay sits in the path.

### The exit ramp (important)

> **If the SoftMedia project ever stops providing the shared key, or it hits its daily
> limit, the only action required is: obtain a free OMDb key and switch the mode to
> "Use My Own Key" (`custom`). No other change is needed and no functionality is lost.**

This guarantee is why the shared key is safe to depend on: the fallback is one setting
away and is always available. The Settings → Metadata → Movies panel shows a standing
helper explaining this whenever the shared key is active.

### Status of the rollout

- ✅ Backend three-mode switch (`softmedia` / `custom` / `disabled`) — implemented in `OMDbProvider.cs`.
- ✅ Settings UI: mode selector, custom-key input, tier picker, daily-usage widget, and the shared-key fallback helper.
- ⏳ **Injecting the real shared key at release time** is blocked on two maintainer decisions:
  1. Which OMDb tier the project funds (see *Open Question #5* in `docs/plans/roadmap/00-roadmap-overview.md`).
  2. There is **no CI/release pipeline yet** (`.github/workflows/` does not exist), so the key-injection step has nothing to attach to. Creating that pipeline is tracked separately.

Until the real key is injected, the committed placeholder `SOFTMEDIA_OMDB_KEY_PLACEHOLDER`
remains in source so the OSS build still compiles; users can use `custom` mode immediately.

---

## Configuring the bundled key (maintainer / release)

## Where to Add the Key

### Location
**File**: `src/SoftMedia.Server/appsettings.json`

**Section**:
```json
"OMDb": {
    "SoftMediaApiKey": "YOUR_ACTUAL_API_KEY_HERE"
}
```

### Current Placeholder
The placeholder value `SOFTMEDIA_OMDB_KEY_PLACEHOLDER` must be replaced with a valid OMDB API key.

---

## How It Works

1. Users can select **OMDb** as their movie metadata provider in Settings → Metadata
2. When OMDb is selected, they choose an API key mode:
   - **SoftMedia Key** (default) - Uses the bundled key from `appsettings.json`
   - **Use My Own Key** - User provides their own key
   - **Disabled** - OMDB is disabled with a warning

---

## Getting an OMDB API Key

1. Visit: https://www.omdbapi.com/apikey.aspx
2. Choose a plan (Free tier: 1,000 requests/day)
3. Register and receive the API key via email
4. Replace the placeholder in `appsettings.json`

---

## Security Note

For production, consider moving the API key to:
- Environment variables: `OMDb__SoftMediaApiKey`
- User secrets (development): `dotnet user-secrets set "OMDb:SoftMediaApiKey" "your-key"`

---

## Code References

- **Provider**: `Services/Metadata/OMDbProvider.cs`
- **Router**: `Services/Metadata/MetadataRouter.cs` (handles API key modes)
- **Settings**: `OMDbApiKeyMode`, `OMDbApiKeyCustom` in database
