# Task 02 — Admin database backup endpoint

**Wave:** B
**Plan:** [feature-shortlist-plan-2026-04-30.md](../../plans/feature-shortlist-plan-2026-04-30.md#wave-b--admin-database-backup-endpoint)
**Severity:** Medium — data-safety net before larger schema changes (Wave C, Wave E) ship.
**Estimated effort:** 1 day. Single PR.
**Branch:** `feat/admin-backup-endpoint`

---

## Background

SDD §2.3 promises "just copy the file" backup. While the server is running and EF holds the SQLite connection open, a raw `File.Copy` of `softmedia.db` can capture pages mid-write or miss data still in the (potentially-enabled) WAL/journal sidecar files. The SQLite C API exposes an online-backup primitive that produces a guaranteed-consistent snapshot under concurrent writers; `Microsoft.Data.Sqlite.SqliteConnection.BackupDatabase` wraps it.

**Note on WAL:** SDD §2.3 claims WAL mode is enabled, but the actual connection string in [appsettings.json](../../../src/SoftMedia.Server/appsettings.json) is `Data Source=softmedia.db` with no `journal_mode=WAL` pragma applied at startup. WAL is therefore not currently in effect. This task does not depend on WAL — `BackupDatabase` is correct under any journal mode. The SDD/code drift on WAL should be resolved in a separate ticket.

Right now there is **no backup endpoint at all**. Admins running this server before bigger changes (Wave C alters the schema; Wave E adds three tables) currently have no in-app safety net.

## Behavior after this task

- A new "Download backup" button appears in the existing Admin Dashboard section of [SettingsPage.tsx](../../../src/SoftMedia.Client/src/pages/SettingsPage.tsx). The button is admin-only (the dashboard already only renders for admins).
- Clicking the button streams a `softmedia-backup-YYYYMMDD-HHMMSS.zip` to the browser. The download is triggered via `URL.createObjectURL(blob)` + `<a download>` rather than a navigation, because the bearer token must travel in the `Authorization` header.
- The zip contains exactly four files:
  - `softmedia.db` — a consistent SQLite snapshot taken via `SqliteConnection.BackupDatabase` (safe under concurrent writes; no scan-pause needed).
  - `settings.json` — the contents of `AppSettings` table as JSON, pretty-printed.
  - `libraries.json` — the contents of the `Libraries` table as JSON. Useful for admins inspecting what paths were configured at backup time.
  - `manifest.json` — `{ schemaVersion, lastAppliedMigration, createdUtc, softMediaVersion }`. `lastAppliedMigration` comes from `_context.Database.GetAppliedMigrationsAsync().Last()`. `softMediaVersion` comes from `Assembly.GetExecutingAssembly().GetName().Version?.ToString()`.
- The zip does **not** contain: cover-art cache, transcoded HLS segments, image proxy cache, FFmpeg binaries. These are reproducible from source media and would balloon backup size for no recovery benefit.
- Endpoint returns `403 Forbidden` for non-admins (standard `[Authorize(Roles = "Admin")]`); `401` for unauthenticated.

## Files to change

### Backend — new files

1. **`src/SoftMedia.Server/Services/Abstractions/IBackupService.cs`**:
   ```csharp
   namespace SoftMedia.Server.Services.Abstractions;

   public interface IBackupService
   {
       /// <summary>
       /// Produces a complete, consistent SoftMedia backup as a zipped stream.
       /// Caller owns the returned stream and must dispose it.
       /// </summary>
       Task<Stream> CreateBackupAsync(CancellationToken cancellationToken);
   }
   ```

2. **`src/SoftMedia.Server/Services/Infrastructure/BackupService.cs`** — implements `IBackupService`.

   **Connection-string source:** the cleanest approach is to ask the already-injected `AppDbContext` for its underlying connection. EF Core's SQLite provider stores the connection as `SqliteConnection`, so we can either (a) reuse it as the source for `BackupDatabase` directly, or (b) read its connection string and open a fresh source connection. Approach (a) is preferred — no second connection, no string handling. EF tolerates a `BackupDatabase` call on its connection; the API does not require an exclusive lock under any journal mode.

   Sketch:
   ```csharp
   using System.IO.Compression;
   using System.Reflection;
   using System.Text.Json;
   using Microsoft.Data.Sqlite;
   using Microsoft.EntityFrameworkCore;
   using SoftMedia.Server.Data;
   using SoftMedia.Server.Services.Abstractions;

   public class BackupService : IBackupService
   {
       private readonly AppDbContext _db;
       private readonly ILogger<BackupService> _logger;

       public BackupService(AppDbContext db, ILogger<BackupService> logger)
       {
           _db = db;
           _logger = logger;
       }

       public async Task<Stream> CreateBackupAsync(CancellationToken ct)
       {
           var memory = new MemoryStream();
           using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
           {
               // 1. Snapshot DB via online-backup API to a temp file, then read into the zip.
               var tempDb = Path.Combine(Path.GetTempPath(), $"sm-bak-{Guid.NewGuid():N}.db");
               try
               {
                   var src = (SqliteConnection)_db.Database.GetDbConnection();
                   if (src.State != System.Data.ConnectionState.Open)
                       await src.OpenAsync(ct);

                   await using (var dst = new SqliteConnection($"Data Source={tempDb}"))
                   {
                       await dst.OpenAsync(ct);
                       src.BackupDatabase(dst);  // online-backup; safe under writers
                   }

                   var dbEntry = zip.CreateEntry("softmedia.db", CompressionLevel.Optimal);
                   await using var dbStream = dbEntry.Open();
                   await using var srcStream = File.OpenRead(tempDb);
                   await srcStream.CopyToAsync(dbStream, ct);
               }
               finally
               {
                   if (File.Exists(tempDb)) File.Delete(tempDb);
               }

               // 2. settings.json
               var settings = await _db.Settings.AsNoTracking().ToListAsync(ct);
               await WriteJsonEntryAsync(zip, "settings.json", settings, ct);

               // 3. libraries.json
               var libraries = await _db.Libraries.AsNoTracking().ToListAsync(ct);
               await WriteJsonEntryAsync(zip, "libraries.json", libraries, ct);

               // 4. manifest.json
               var migrations = await _db.Database.GetAppliedMigrationsAsync(ct);
               var manifest = new
               {
                   schemaVersion = 1,
                   lastAppliedMigration = migrations.LastOrDefault(),
                   createdUtc = DateTime.UtcNow,
                   softMediaVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString()
               };
               await WriteJsonEntryAsync(zip, "manifest.json", manifest, ct);
           }
           memory.Position = 0;
           return memory;
       }

       private static async Task WriteJsonEntryAsync<T>(
           ZipArchive zip, string name, T payload, CancellationToken ct)
       {
           var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
           await using var stream = entry.Open();
           await JsonSerializer.SerializeAsync(stream, payload,
               new JsonSerializerOptions { WriteIndented = true }, ct);
       }
   }
   ```

   **Important comment to include in the file** (paraphrasing the safety guarantee):
   ```
   // SQLite's online-backup API is safe under concurrent writers — it
   // copies pages atomically and re-reads any pages dirtied during the
   // copy. No scan-pause or maintenance-mode flag is required, and a
   // future reader should not add one defensively. This is true under
   // any journal mode (DELETE, WAL, MEMORY).
   ```

3. **[src/SoftMedia.Server/Extensions/ServiceCollectionExtensions.cs](../../../src/SoftMedia.Server/Extensions/ServiceCollectionExtensions.cs)** — add inside `AddMediaServices` near other Infrastructure registrations (around line 192):
   ```csharp
   services.AddScoped<IBackupService, BackupService>();
   ```

### Backend — modify

4. **[src/SoftMedia.Server/Controllers/AdminController.cs](../../../src/SoftMedia.Server/Controllers/AdminController.cs)** — inject `IBackupService` into the constructor and add the endpoint:
   ```csharp
   /// <summary>
   /// Streams a complete SoftMedia backup as a zip. Admin-only.
   /// </summary>
   [HttpGet("backup")]
   public async Task<IActionResult> DownloadBackup(CancellationToken ct)
   {
       var stream = await _backupService.CreateBackupAsync(ct);
       var filename = $"softmedia-backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip";
       _logger.LogInformation("Admin {User} downloaded backup", User.Identity?.Name);
       return File(stream, "application/zip", filename);
   }
   ```
   The class-level `[Authorize(Roles = "Admin")]` at [AdminController.cs:20](../../../src/SoftMedia.Server/Controllers/AdminController.cs#L20) already gates this.

### Frontend

5. **`src/SoftMedia.Client/src/services/adminService.ts`** — add:
   ```ts
   downloadBackup: async (): Promise<{ blob: Blob; filename: string }> => {
       const response = await api.get('/admin/backup', { responseType: 'blob' });
       const disposition = response.headers['content-disposition'] ?? '';
       const match = /filename=([^;]+)/i.exec(disposition);
       const filename = match?.[1]?.replace(/"/g, '').trim()
           ?? `softmedia-backup-${new Date().toISOString().slice(0, 10)}.zip`;
       return { blob: response.data, filename };
   },
   ```

6. **[src/SoftMedia.Client/src/pages/SettingsPage.tsx](../../../src/SoftMedia.Client/src/pages/SettingsPage.tsx)** — in the `AdminDashboard` component, add a "Backup" card. Use the same Tailwind shape as the existing API Usage card (`bg-white/5 rounded-xl p-6 border border-white/10`). The button:
   - `<button type="button">` (per universal-client rule).
   - Pairs `hover:bg-primary/90` with `focus-visible:ring-2 focus-visible:ring-blue-400`.
   - Shows a spinner while the mutation is pending.
   - On click, runs a `useMutation` that calls `adminService.downloadBackup()`, then triggers the download:
     ```ts
     const url = URL.createObjectURL(result.blob);
     const a = document.createElement('a');
     a.href = url;
     a.download = result.filename;
     a.click();
     URL.revokeObjectURL(url);
     ```
   - On error, shows a `toast.error('Backup failed: ' + message)`.
   - i18n: wrap user-visible strings in `t(...)` if the surrounding section already uses `useTranslation`.

### Tests

7. **`src/SoftMedia.Server.Tests/Services/Infrastructure/BackupServiceTests.cs`** (new file) — xUnit:
   - `CreateBackupAsync_ProducesZipWithFourEntries` — call the service, assert the returned stream opens as a `ZipArchive` with entries `softmedia.db`, `settings.json`, `libraries.json`, `manifest.json`.
   - `CreateBackupAsync_DbEntry_IsValidSqlite` — extract the `softmedia.db` entry, open it with `SqliteConnection`, run `SELECT COUNT(*) FROM sqlite_master`, assert >0 (proves the byte stream is a real SQLite database).
   - `CreateBackupAsync_DbEntry_PreservesUserCount` — pre-seed the in-memory DB with N users, take a backup, open the dumped DB, assert `SELECT COUNT(*) FROM Users` returns N.
   - `CreateBackupAsync_ManifestIncludesLastMigration` — assert `manifest.json` parses and `lastAppliedMigration` is non-null.

   Setup: use the same in-memory SQLite fixture pattern as other repository tests. Write to a `MemoryStream`, then re-open as `ZipArchive` with `ZipArchiveMode.Read`.

8. **`src/SoftMedia.Server.Tests/Controllers/AdminBackupAuthTests.cs`** (new file) — integration-style:
   - Anonymous `GET /api/v1/admin/backup` returns `401`.
   - Authenticated non-admin returns `403`.
   - Authenticated admin returns `200` with `Content-Type: application/zip` and a non-empty body.

## Acceptance criteria

- A logged-in admin clicking "Download backup" receives a `softmedia-backup-...zip` file.
- Unzipping it produces exactly the four files listed above.
- Loading the zipped `softmedia.db` in a SQLite GUI shows the same row counts as the live database (within the time delta of seconds).
- A non-admin user (`Role = User`) calling the endpoint receives 403, not the file.
- `dotnet test` passes; all four backup-service tests and three auth tests are present and green.
- No EF migration introduced.

## Out of scope (deferred to a follow-up PR)

- **Restore endpoint.** Restore is destructive (it overwrites the live DB). It needs:
  - A maintenance-mode flag that pauses the file watcher, scan queue, and refresh-token cleanup.
  - A confirmation flow with the admin password.
  - Schema-version compatibility checking against `manifest.json.lastAppliedMigration`.
  Track separately.
- **Scheduled backups.** A nightly cron-style backup to a configured directory is a different feature; this PR is on-demand only.
- **Encrypted backups.** Out of scope for v1; the file is sensitive (password hashes, refresh-token hashes) and the user is responsible for storing it safely. Document this in a `<p class="text-xs text-gray-500">` warning under the button: "Backups contain hashed credentials. Store securely."
