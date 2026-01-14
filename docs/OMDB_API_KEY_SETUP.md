# OMDB API Key Setup

This document describes how to configure the bundled SoftMedia OMDB API key.

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
