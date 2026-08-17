*Read this in [Español](README.es.md).*

# Jellyfin Scheduled Access

A Jellyfin plugin that restricts what content a user can see **based on the day of the week**, using library tags.

Example: user `test` only sees content tagged `sunday` on Sundays, and their full library the rest of the week.

---

## Requirements

| | Version | Note |
|---|---|---|
| Jellyfin Server | **10.11.x** | The `targetAbi` must match, or the plugin shows up as *NotSupported* |
| .NET SDK | **9.0** | Jellyfin 10.11 targets `net9.0`; 10.10 targeted `net8.0` |

The `Jellyfin.Controller` / `Jellyfin.Model` packages in the `csproj` must be **pinned to the server's exact version**.

---

## Installation

### From the repository (recommended)

**1. Add the repository.** Under *Dashboard → Plugins → **Repositories*** → **+ New repository**:

| Field | Value |
|---|---|
| Name | `Scheduled Access` (or whatever you like) |
| URL | `https://raw.githubusercontent.com/bruce-rgb/jellyfin-plugin-scheduled-access/main/manifest.json` |

**2. Install it.** Switch to the ***Catalog*** tab — not *Repositories*, which only lists sources — and look for **Scheduled Access** under the *General* category.

**3. Restart the server.** Jellyfin only loads new plugins at startup.

Prefer this over copying files by hand: the folder is created by the service itself, with correct ownership and a consistent `meta.json`, which avoids both pitfalls described under [Debugging](#debugging).

> **If the plugin doesn't show up in the catalog**, check in this order:
> 1. That you're looking at *Catalog*, not *Repositories*.
> 2. That the URL responds — open it in a browser, it should return JSON.
> 3. That the manifest's `targetAbi` isn't higher than your server version.
> 4. Hard-refresh the browser (`Ctrl+Shift+R`). The web UI caches the package list client-side, so a stale cache hides a repository you just added.

### Manual installation

Download the `.zip` from [Releases](https://github.com/bruce-rgb/jellyfin-plugin-scheduled-access/releases), extract it into a folder under `<datadir>/plugins/` and restart. Per-platform details are under [Deploying to a real server](#deploying-to-a-real-server-docker).

---

## How it works

Jellyfin already evaluates each item's visibility against two lists in the user policy. This is the server's actual logic (`BaseItem.IsVisibleViaTags`, v10.11.11):

```csharp
var allTags = GetInheritedTags();
if (user.GetPreference(PreferenceKind.BlockedTags).Any(i => allTags.Contains(i, ...)))
    return false;                                    // BlockedTags wins, evaluated first

var parent = GetParents().FirstOrDefault() ?? this;
if (parent is UserRootFolder or AggregateFolder or UserView)
    return true;                                     // root level skips the AllowedTags check

var allowedTagsPreference = user.GetPreference(PreferenceKind.AllowedTags);
if (!skipAllowedTagsCheck && allowedTagsPreference.Length != 0 &&
    !allowedTagsPreference.Any(i => allTags.Contains(i, ...)))
    return false;                                    // strict allowlist
```

The plugin **does not filter content itself**: it only rewrites the user's `AllowedTags` / `BlockedTags` according to the day, and lets the server do the rest.

Two consequences worth knowing:

- **`GetInheritedTags()`**: tags are inherited from parent folders and collections. You can tag a whole folder instead of item by item.
- **Root level skips the filter**: libraries stay visible on the home screen; what gets filtered is their contents.

### The two modes

| Mode | Field | Behaviour | Risk |
|---|---|---|---|
| `Block` — *hide content with these tags* | `BlockedTags` | Hides only what is tagged | **Fails open**: new untagged content stays visible |
| `AllowOnly` — *show only content with these tags* | `AllowedTags` | Hides everything **without** the tag | **Fails closed**: new untagged content disappears |

If the goal is to genuinely restrict, `AllowOnly` is the safe mode. If you only want to set aside a few specific things, `Block` requires far less tagging work.

### Restoration: why snapshots exist

Before applying the first restriction to a user, the plugin saves a **snapshot** (`PolicySnapshot`) of their original `AllowedTags` / `BlockedTags`, and persists it in the configuration XML.

This isn't incidental — it's the critical safety mechanism. Without it, if the server were shut down on a Sunday with the restriction in place, the user would stay restricted **indefinitely**: there would be no way to know their original state. With the snapshot on disk, Monday's run (or the next startup) undoes it.

The desired state is **always computed from the snapshot**, never from the current policy. That makes the task idempotent: running it ten times doesn't accumulate tags.

#### Two invariants that must hold

Both came out of real bugs, and breaking either one leaves users permanently restricted:

**1. Restoration is driven by snapshots, not by rules.**

`ExecuteAsync` works in two phases: first it applies the rules that are in effect today and records which users ended up restricted; then it walks **the snapshots** and undoes every one that doesn't back a current restriction.

The intuitive approach would be to walk the rules and restore the ones that don't apply today — and it's wrong. If you delete a rule, there's nothing left to walk: the user is never visited and their restriction is never undone. Walking snapshots covers deleted rules, unchecked days, changed users and a disabled plugin all at once.

A snapshot is only discarded if restoration **succeeded**. If it fails, the snapshot survives and the next trigger retries.

**2. Snapshots are never accepted from the client.**

They are server state. `Plugin.UpdateConfiguration` discards any that arrive in the `POST` and keeps its own:

```csharp
if (configuration is PluginConfiguration incoming)
{
    incoming.Snapshots = Configuration.Snapshots;   // read BEFORE base.UpdateConfiguration
}
```

Without this, the config page reads them when it opens and sends them back on save. If the task created a snapshot after the page loaded, saving deletes it; the next run finds none and takes a fresh one **against the already-restricted policy**, recording the restricted state as if it were the original.

The symptom is treacherous: the log reports `Politica restaurada` perfectly normally, but restores to the corrupted state and the user stays restricted. You spot it in the log as a second snapshot for the same user with a non-zero count:

```
Instantanea de politica guardada para "test" (permitidas=0, ...)   ← correct original
Instantanea de politica guardada para "test" (permitidas=1, ...)   ← corrupt: captured the restricted state
```

If this happens the original data is lost, and the tags must be cleared by hand under **Users → *(user)* → Parental Control**.

### Triggers

The `Aplicar restricciones por dia` task runs at three moments:

| Trigger | Purpose |
|---|---|
| `StartupTrigger` | Fixes state if the server was off when the day changed |
| `DailyTrigger` (00:00) | The actual day change |
| `IntervalTrigger` (1 h) | Safety net against sleep or clock changes |

Disabling the plugin (`Enabled = false`) **leaves no restrictions dangling**: the next run restores every pending snapshot and discards them.

---

## Configuration

**Dashboard → Plugins → Scheduled Access**

1. Tick *Activar restricciones por día*.
2. **Add rule**: pick a user, check the days, choose the mode and enter comma-separated tags.
3. Save.

On save the plugin **queues the task automatically**, so changes take effect within seconds instead of waiting for midnight. This is done by `Plugin.UpdateConfiguration`, which is `virtual` on `BasePlugin<T>`:

```csharp
public override void UpdateConfiguration(BasePluginConfiguration configuration)
{
    base.UpdateConfiguration(configuration);
    _taskManager.QueueIfNotRunning<ApplyTagScheduleTask>();
}
```

`ITaskManager` is injected through the plugin constructor: Jellyfin instantiates plugins through its DI container, so it accepts services beyond the two mandatory parameters.

> After it applies, **refresh the client or sign out and back in**: the web UI caches views and may keep showing the previous content even though the policy already changed.

You can also run it manually from **Dashboard → Scheduled Tasks → Aplicar restricciones por dia**.

---

## Development

### Building

```bash
dotnet publish --configuration Debug Jellyfin.Plugin.ScheduledAccess.sln
```

The project builds with `TreatWarningsAsErrors` and every analyzer enabled (`AnalysisMode=AllEnabledByDefault` + StyleCop + `jellyfin.ruleset`). Any warning breaks the build; that's intentional.

Two rules that bite when writing configuration types:

- **SA1402 / SA1649**: one class per file, and the file name must match. An `enum` may accompany a class.
- **CA1819** (don't expose arrays in properties) is **not disabled** in the ruleset. The configuration types **use arrays anyway**, with a narrow documented suppression: they must round-trip through `XmlSerializer` (config on disk) and `System.Text.Json` (the web page), and read-only collections aren't reliable with the latter. Silently losing rules on save would be worse than the design smell.

### Deploying locally

VS Code tasks (`Ctrl+Shift+P → Tasks: Run Task`):

| Task | What it does |
|---|---|
| **`deploy`** | Builds and deploys. The one you'll normally use (also `Ctrl+Shift+B`) |
| **`build`** | Builds only — no deploy, no UAC prompt |
| **`tail-log`** | Follows the server log live |
| **`tail-log-plugin`** | Same, filtered to the plugin's lines |

`Ctrl+Shift+B` is a shortcut to `deploy`, as the default build task. Both log tasks keep running until you stop them from the terminal panel.

The logic lives in [scripts/deploy-local.ps1](scripts/deploy-local.ps1), which you can also run by hand. It groups **stop → copy → permissions → start into a single elevation**, for two reasons:

1. Jellyfin keeps the DLL locked while running; copying with the service up fails with `IOException`.
2. Stopping and starting a service requires administrator rights. Grouping avoids chaining several UAC prompts.

**Accepting the UAC prompt is manual.** There's no way around it with Jellyfin installed as a service.

> **`meta.json` must carry the assembly's real version.** The script reads it from the freshly built binary instead of hardcoding it, and that's not cosmetic.
>
> Jellyfin **registers** the plugin by the manifest version, but the dashboard **displays and sends** the assembly version. If they diverge, `DELETE /Plugins/{guid}/{version}` returns **404**: it's looking for a version it has no record of. The symptom misleads, because the plugin loads and works fine; what breaks is uninstalling or updating from the dashboard.
>
> This happened when only the build output was copied: the DLL got updated while `meta.json` kept the version from the first deploy. You spot it by comparing the startup log against the dashboard:
>
> ```
> Loaded assembly "...Version=1.0.0.0..."      ← the assembly
> Loaded plugin: "Scheduled Access" "0.0.0.0"  ← the manifest: they don't match
> ```

The `.pdb` is copied too: that's what enables breakpoints.

> **Why the task runs `icacls` at the end.** Because of Windows' `CREATOR OWNER` rule, the plugin folder ends up owned by whoever creates it — you, when deploying. The service runs as `NT AUTHORITY\NETWORK SERVICE` and only inherits `BUILTIN\Users:(RX)`: read and execute, **no delete permission**.
>
> The symptom is deceptive, because the plugin **loads without trouble**. What fails is **uninstalling or updating from the dashboard**: the service can't delete files it doesn't own. Diagnose by comparing the ACLs:
>
> ```powershell
> icacls "$env:ProgramData\Jellyfin\Server\plugins"
> icacls "$env:ProgramData\Jellyfin\Server\plugins\Jellyfin.Plugin.ScheduledAccess"
> ```
>
> If `NETWORK SERVICE` is missing from the second one, this is it. Fix with:
>
> ```powershell
> icacls "<plugin folder>" /grant "NT AUTHORITY\NETWORK SERVICE:(OI)(CI)F" /T
> ```
>
> Plugins installed from a repository don't suffer from this: the service creates them, so it already owns them.

Paths are configured in [.vscode/settings.json](.vscode/settings.json). `jellyfinDataDir` must point at the server's **actual** data dir, which depends on the install mode:

| Installation | Data dir |
|---|---|
| Windows service | `C:\ProgramData\Jellyfin\Server` |
| Tray / user app | `%LOCALAPPDATA%\jellyfin` |

To confirm it without guessing, read the service's actual parameters:

```powershell
Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\JellyfinServer\Parameters" |
    Select-Object Application, AppParameters
```

### Debugging

**Rule out first: has the task run since you saved?** Saving queues it automatically, but if you edited the XML by hand, or queuing failed, nothing has been applied. It's the most common cause of "it doesn't work".

Three places to look, in order:

**1. The configuration XML** — tells you what was saved and what was applied:

```powershell
Get-Content "C:\ProgramData\Jellyfin\Server\plugins\configurations\Jellyfin.Plugin.ScheduledAccess.xml" -Raw
```

An empty `<Snapshots />` means **no restriction was ever applied**. If there's a `<PolicySnapshot>`, the plugin has already touched that user's policy.

**2. The server log** — the task records every action:

```powershell
$log = Get-ChildItem "C:\ProgramData\Jellyfin\Server\log" -Filter "log_*.log" |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
Select-String -Path $log.FullName -Pattern "Restriccion aplicada|Instantanea|Politica restaurada"
```

Expected output:

```
ApplyTagScheduleTask: Instantanea de politica guardada para "test" (permitidas=0, bloqueadas=0)
ApplyTagScheduleTask: Restriccion aplicada a "test" para Sunday en modo AllowOnly con 1 etiquetas
```

**3. The user's policy** under Dashboard → Users → *(user)* → Parental Control, to see the tags the plugin wrote.

After applying, **refresh the client or sign in again**: the web UI caches views and may keep showing the old content.

#### Breakpoints

The server is an official binary, not built from source, so debugging is by **attach**, not launch: run `deploy`, wait for startup, then launch *Adjuntar a Jellyfin* ([.vscode/launch.json](.vscode/launch.json)).

Since the service runs as `NT Authority\NetworkService`, **VS Code must be started as administrator** to attach.

---

## Deploying to a real server (Docker)

### 1. Package

```powershell
.\scripts\package.ps1
```

Builds in **Release** and leaves `dist/Jellyfin.Plugin.ScheduledAccess/` with just the DLL and a complete `meta.json`. It deliberately excludes the `.pdb` (debug symbols), the `.xml` (documentation) and the `.deps.json`: the server doesn't need them to load a plugin.

`meta.json` is written by hand because the one Jellyfin generates on a hot install leaves `version: 0.0.0.0` and the descriptive fields empty. Its `targetAbi` is `10.11.0.0`, not `10.11.11.0`, so it covers the whole 10.11.x series rather than a single patch.

> **The ABI must match the target server.** A plugin built against 10.11 won't load on 10.10: it shows as *NotSupported*. The DLL is pure IL, so architecture (x86_64, ARM) is irrelevant — only the version matters.

### 2. Locate the config volume

The plugin goes in `<config>/plugins/`, where `<config>` is the host path mapped to `/config` in the container:

```bash
docker inspect -f '{{range .Mounts}}{{.Source}} -> {{.Destination}}{{"\n"}}{{end}}' jellyfin
```

### 3. Copy and fix ownership

```bash
scp -r dist/Jellyfin.Plugin.ScheduledAccess user@server:/tmp/

# on the server, with <config> replaced by the real path
sudo mv /tmp/Jellyfin.Plugin.ScheduledAccess <config>/plugins/
```

**Ownership must match the user running inside the container**, or the plugin won't be read. Instead of guessing the UID, copy it from a folder Jellyfin already uses:

```bash
sudo chown -R --reference=<config>/config <config>/plugins/Jellyfin.Plugin.ScheduledAccess
sudo chmod -R u+rwX,go+rX <config>/plugins/Jellyfin.Plugin.ScheduledAccess
```

> **Careful with `PUID`/`PGID`.** They're a **linuxserver.io** convention. The **official** `jellyfin/jellyfin` image ignores them entirely: the user is controlled by the compose `user:` key, and without it the container runs as **root**. A `docker-compose.yml` using the official image with `PUID=1000` is misleading — those variables do nothing, and running `chown 1000` would be exactly the wrong move.

### Time zone

The plugin decides the day with `DateTime.Now.DayOfWeek`, which is the **container's local time**. Set `TZ` in the compose file:

```yaml
environment:
  - TZ=America/Mexico_City
```

Without `TZ` the container runs in UTC and the day change happens offset from your real schedule — "Sunday" would start and end at the wrong hour.

### 4. Restart and verify

```bash
docker restart jellyfin
docker logs jellyfin 2>&1 | grep -i scheduledaccess
```

Expected output:

```
Loaded assembly "Jellyfin.Plugin.ScheduledAccess, Version=1.0.0.0, ..."
Loaded plugin: "Scheduled Access" "1.0.0.0"
```

If nothing shows up, suspect in this order: file ownership → incompatible `targetAbi` → wrong path (the mapped volume isn't the one you thought).

---

## Publishing a release

Jellyfin has no store and no approval process: **a plugin repository is just a URL to a JSON file**. Users add it under *Dashboard → Plugins → Repositories* and your plugins show up.

Users install by adding this URL:

```
https://raw.githubusercontent.com/<owner>/<repo>/main/manifest.json
```

### Automatic publishing

```bash
git tag v1.0.0.0
git push origin v1.0.0.0
```

The [.github/workflows/release.yml](.github/workflows/release.yml) workflow builds, publishes the zip to Releases, computes the checksum and commits the updated `manifest.json` to `main`. The version comes **from the tag**, and propagates from there to the assembly, `meta.json`, the zip name and the manifest, so they can't drift apart.

Order matters: the zip is uploaded **before** the manifest is committed, because the `sourceUrl` it contains must already exist when someone reads it.

### Manual publishing

```powershell
.\scripts\package.ps1 -Version 1.2.0.0 -Changelog "What changed"
```

Generates the zip in `dist/`, updates `manifest.json`, and you upload the zip to the matching release.

> **The zip is not reproducible**: it embeds timestamps, so every build yields a different MD5. If you re-run the script after uploading the zip, the manifest checksum will no longer match the published binary and the server will reject the download. Upload **exactly** the zip from the run that generated the manifest. This can't happen in CI because both come from the same run.

### Format details that take a while to discover

- The **checksum is the zip's MD5**, lowercase. The server validates the download against it; if it doesn't match, the error the user sees doesn't explain why.
- The zip holds its files **at the root**, not inside a subfolder: Jellyfin extracts it straight into the plugin directory.
- JSON files are written **without a BOM**. `Out-File -Encoding utf8` on Windows PowerShell 5.1 adds one, which breaks both `ConvertFrom-Json` on re-read and anything consuming the manifest.
- The manifest must **always be an array**, even for a single plugin.
- The `guid` must be unique across the ecosystem, and **must match** `Plugin.Id` in the code.

### Licence obligation

The binary links against GPLv3 packages, so **it is GPLv3**. Distributing it requires publishing the source: the repository must be **public**.

---

## Native alternative: you may not need this plugin

Jellyfin already ships day-of-week restriction, no plugin required: `UserPolicy.AccessSchedules`, under **Users → *(user)* → Access Schedule**.

```
AccessSchedule:   DayOfWeek (DynamicDayOfWeek), StartHour, EndHour
DynamicDayOfWeek: Sunday=0 … Saturday=6, Everyday=7, Weekday=8, Weekend=9
```

**If what you want is to stop someone signing in on Sundays, or outside certain hours, use that and forget this plugin.** It's all-or-nothing: it blocks the whole session and doesn't distinguish between libraries or tags.

This plugin only adds value when the user **should** be able to sign in that day, but see a subset of the content.

---

## Status and known limitations

- **The plugin's UI and log messages are in Spanish.** The configuration page and the scheduled task name are not localised yet. Contributions welcome.
- **Rules apply to administrator accounts too** (verified on 10.11.11). Unlike other Jellyfin parental controls, tag filtering doesn't exempt admins: if you apply a rule to yourself, you'll see the same trimmed library as anyone else. Be careful not to lock yourself out of content you need.
- The version shown by the plugin manager reads `0.0.0.0` on manual installs, because it comes from the manifest rather than the assembly. Cosmetic, and only during development.
- Rules don't validate overlaps: if two rules target the same user and day, the last one applied wins.

## Licence

GPLv3 — see [LICENSE](LICENSE). Jellyfin plugins link against GPLv3 packages, so the resulting binary is GPLv3 even if the source carries a more permissive licence.
