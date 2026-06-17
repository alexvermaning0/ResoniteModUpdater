# ModUpdater

A [ResoniteModLoader](https://github.com/resonite-modding-group/ResoniteModLoader) mod that keeps your **other**
mods up to date. It handles both **RML mods** (`.dll` in `rml_mods`) and **MonkeyLoader mods** (`.nupkg` in
`MonkeyLoader/Mods`), and also **notifies** you when **MonkeyLoader itself** has a newer release (without touching
the loader). Each mod can be left on **Latest** (newest stable release) or **pinned to any specific version** —
including older ones (a downgrade) — or set to **Hold** (never touched). Changes are staged in place, ready to load
on your next Resonite restart. It never touches itself.

Sources are merged: for each mod it consults both the mod's own GitHub `Link` **and** the community
[resonite-mod-manifest](https://github.com/resonite-modding-group/resonite-mod-manifest), and uses **whichever has
the newer version** — so you get coverage for mods with no usable Link (or non-GitHub hosts like Codeberg) while
still catching fresh GitHub releases the manifest hasn't indexed yet.

<img width="3840" height="2054" alt="The Mod Updater dash tab" src="https://github.com/user-attachments/assets/f23b0278-a0fa-4f83-942f-29d78736cf9d" />

## How it works

1. **Enumerate** – at startup (after a short delay) and on demand: RML mods via `ModLoader.Mods()`; MonkeyLoader
   mods by reading the `.nuspec` (id/version/repo) inside each `MonkeyLoader/Mods/*.nupkg`; plus MonkeyLoader
   itself (notify-only).
2. **Check** – versions come from the repo's public `releases.atom` RSS feed (no token, **not** rate-limited) and
   the community manifest, merged newest-first. "Latest" = newest tag that isn't a prerelease (alpha/beta/rc/pre/…).
   The GitHub REST API is touched only when actually downloading from a GitHub release.
3. **Resolve target** – per your per-mod selection: *Latest* = newest stable; a *pinned tag* = exactly that release
   (up- or downgrade); *Hold* = untouched. Downloads prefer the manifest's direct, sha256-checked artifact URL when
   that version is listed (no API), else the GitHub release asset.
4. **Stage** – a loaded file usually can be *moved* (not overwritten): the old file goes to
   `_ModUpdater/backups/<Mod>/…​.bak` and the new `.dll`/`.nupkg` is written in its place, taking effect on the
   **next launch** (the `.bak` is your rollback backup). Some mods' files are held by a *persistent* lock all
   session and can't be swapped in-process — for those the download is **staged to `_ModUpdater/pending/`** and a
   small detached helper waits for Resonite to fully close (everything unlocked then) and applies the swap, so it's
   ready next launch. MonkeyLoader itself is **never** staged — it's notify-only (a button points you at its
   GitHub release).
5. **Notify & control** – an in-world toast summarizes what changed, and an **"Updater" tab is added to the Dash**
   (next to Settings), so it's cursor-usable in desktop mode as well as VR. Each mod is a row:

   `Name (reason if any) · installed → latest · [ version field ] · ◄ ► · [ Update now ]`

   - The **version field** and **◄ / ►** cycle the selection: **Latest → Hold → each release**.
   - **Update now** is visible but disabled unless there's a change to apply; it reads **Downgrade** when the
     pinned target is older than installed. Pressing it stages just that mod (independent of Auto-download).
   - A **↺** revert button appears on mods that have a backup.
   - The header has **Check now** and an **Auto-download** toggle. Staged changes apply on the next restart.

## Build & install

```powershell
dotnet build -c Release
powershell -ExecutionPolicy Bypass -File .\copy-to-mods.ps1   # may need an elevated shell for Program Files
```

Then (re)start Resonite. If your install isn't at the default Steam path, pass `-p:ResonitePath=...` to the build
and `-ResonitePath ...` to the copy script.

## Settings (ResoniteModLoader config)

| Key | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `true` | Master switch. |
| `AutoDownload` | `false` | Download & stage target DLLs. Off by default (notify-only) so the first run never overwrites a modified/forked mod before you've reviewed it. Toggle it from the panel header. |
| `CheckOnStartup` | `true` | Run a check shortly after launch. |
| `StartupDelaySeconds` | `30` | Delay before the first check. |
| `VersionPins` | `""` | Per-mod version selection (JSON, managed by the panel's `< >` selector). Absent = Latest. |
| `GitHubToken` | `""` | Optional token to raise the GitHub API rate limit. |

## Forked or locally-modified mods (important)

The updater identifies a mod only by its GitHub `Link` + reported `Version`. A mod whose DLL you've **edited
locally** or that is a **fork still pointing at the original repo** will be compared against that upstream repo — so
on *Latest* it can get replaced by the stock upstream build if upstream's version is higher.

To protect such a mod, set it to **Hold** with the panel's `< >` selector (or pin it to a specific release). Held
mods are never touched. Because the startup check runs before the panel opens, auto-download is **off by default**
for exactly this reason: do your first launch, set any forks/local builds to Hold, *then* turn auto-download on.

> Note: because the community manifest is now also consulted, a mod that has **no usable GitHub Link** may still get
> a source from the manifest (matched by name). So **Hold** — not "no source" — is the reliable way to keep a
> modified mod from being replaced.

(If the upstream DLL ever does replace one, your previous DLL is preserved in `rml_mods/_ModUpdater/backups/<Mod>/`
and a **Revert** button restores it.)

## Notes & limits

- Only mods whose `Link` is a GitHub repo **with a `.dll` release asset** can be auto-updated. Others are shown as
  `no source` / `manual update`.
- Version comparison normalizes tags like `v1.2.3`; if a tag can't be compared to the installed version the mod is
  left alone and shown as `unknown` (never auto-downloaded).
- **Rate limit:** version checks use the RSS feed and cost **no** GitHub API quota, so you can check freely.
  Only *downloads* use the REST API (anonymous cap 60/hour). If you stage many updates at once, set `GitHubToken`
  (a fine-grained PAT with public read is enough) to lift the cap to 5000/hour.
- Backups: the last 3 versions per mod are kept under `rml_mods/_ModUpdater/`. Pending-restart state lives in
  `rml_mods/_ModUpdater/state.json`.
- **Opening the UI:** open the **Dash** and pick the **Updater** tab (purple, near Settings). It's added by
  Harmony-patching `UserspaceScreensManager`, so it regenerates whenever the dash is (re)built.
