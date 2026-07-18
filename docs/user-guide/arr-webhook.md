# Sonarr / Radarr Scan Webhook

When Sonarr or Radarr imports a file, SoftMedia won't notice until its next
scheduled scan. The scan webhook closes that loop: the *arr tool pings SoftMedia
after each import and the owning library is scanned immediately.

## 1. Create a scan token

In SoftMedia: **My Account → API Tokens → Create token**, and select only the
**Trigger scans** (`write:library`) scope. This token can trigger scans and
nothing else — it cannot read your library, change playback state, or perform
admin actions, so it is safe to store in the *arr config.

## 2. Configure the *arr connection

In Sonarr/Radarr: **Settings → Connect → + → Webhook**

| Field | Value |
|-------|-------|
| Name | SoftMedia |
| Triggers | **On Import**, **On Upgrade** (others optional) |
| URL | `http://<softmedia-host>:5011/api/v1/scan` |
| Method | POST |
| Headers | `Authorization: Bearer <your token>` |

The webhook body Sonarr/Radarr sends is ignored except for the path — SoftMedia
scans the library that owns the imported file's location. If the tool cannot
send a path, the pathless call scans **all** libraries (the queue deduplicates,
so repeated pings while a scan runs do not stack extra work).

To target a specific library from a generic automation, POST JSON:

```json
{ "path": "D:\\Media\\TV\\Some Show\\Season 01" }
```

## Behaviour and limits

- The path must resolve **inside a configured library root**; anything else is
  rejected (the webhook is not a general filesystem probe).
- The whole owning library is scanned, not just the one folder — scans are
  deduplicated and serialized, which import volumes tolerate comfortably.
- Responses: `202 Accepted` with the enqueued job info; `401` without a valid
  token; `403` for a token lacking the `write:library` scope; `404` for a path
  outside every library.
