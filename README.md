# SteamWorkshopAgent

SteamWorkshopAgent is a local stdio MCP server for publishing RimWorld mod releases to Steam Workshop from an already-prepared GitHub release. Its preferred backend uses the Steam desktop client's authenticated RimWorld session and never handles a Steam password or Steam Guard code.

The server is aimed at the local mod-development workflow: build the mod in Release mode, stage the generated mod folder, and upload the Workshop update through the authenticated desktop Steam session without opening RimWorld's mod UI.

## Documentation

- [README.md](README.md): user-facing overview and quick start
- [AGENTS.md](AGENTS.md): local MCP redeploy convention for this workspace
- [Architecture Enhancements](docs/ARCHITECTURE_ENHANCEMENTS.md): hybrid Steamworks, RimWorld companion, SteamCMD, and Workshop read-back architecture

## Highlights

- Inspect RimWorld mod metadata from a source repository or an already-deployed mod folder using `About/About.xml`, optional `About/PublishedFileId.txt`, `LoadFolders.xml`, and optional `Directory.Build.props`.
- Use the fixed RimWorld Steam app id `294100` and the mod Workshop id from `About/PublishedFileId.txt` when present.
- Create the initial private Steam Workshop item for a new local mod from either a source repository or an already-built mod folder, and write the returned id to `About/PublishedFileId.txt`.
- Read GitHub release notes through `gh release view`.
- Generate a dry-run Workshop release plan before uploading.
- Publish content through a detached Steamworks helper using the logged-on Steam desktop session.
- Fall back before submission to a minimal headless RimWorld plus RimBridge companion when Steam does not authenticate the standalone helper.
- Keep SteamCMD `+workshop_build_item` as an explicit emergency backend.
- Submit RimWorld Workshop category tags such as `Mod` and `1.6` through the same local Steamworks UGC path RimWorld uses.
- Preserve the existing Workshop page description unless `updateDescription` is explicitly enabled.
- Read the current main Workshop page title and description through Steam's public item details API.
- Update the main Workshop page description directly with a metadata-only SteamCMD VDF, without rebuilding mod content or changing tags.
- Override the Steam Workshop release changenote at publish time when you want a concise hand-written note instead of the GitHub release body.
- Submit a changenote update for an already-published Workshop item through local Steamworks.

## Requirements

- .NET 10 SDK to build from source
- GitHub CLI `gh` for reading release notes
- A running, online, logged-on Steam desktop client
- RimWorld and RimBridgeServer for the in-game authenticated fallback
- SteamCMD only when explicitly using the emergency `steamcmd` backend
- A RimWorld mod source repository with a Release build target that supports `RIMWORLD_MOD_DIR` for release publishing
- For initial `new-mod` uploads, either a source repository or an already-built RimWorld mod folder with `About/About.xml`, `About/Preview.png`, and versioned `Assemblies` content

Install SteamCMD on this Mac with:

```sh
brew install --cask steamcmd
```

SteamWorkshopAgent does not store Steam passwords or Steam Guard codes. It passes only the Steam username to SteamCMD. Pass that username to the publish tool or set:

```sh
export STEAMCMD_USER=your_steam_username
```

When running as an MCP server, the client process may not inherit your terminal environment. If `steamUser` is omitted and `STEAMCMD_USER` is missing from the MCP process environment, the agent also probes your configured shell startup files and reads only that username value.

Normal publishing does not use SteamCMD authentication. If the Steamworks and RimWorld fallback paths are unavailable and you deliberately select `backend=steamcmd`, authenticate SteamCMD in a real terminal:

```sh
steamcmd +login your_steam_username +quit
```

Enter the password and Steam Guard token there. SteamCMD stores a reusable login token in Steam's config directory, not in this MCP server. On this Mac the relevant file is:

```text
~/Library/Application Support/Steam/config/config.vdf
```

That token is used only by the explicit SteamCMD fallback. Never pass passwords or Steam Guard codes to SteamWorkshopAgent, MCP tools, or chat.

Routine updates preserve the Workshop item's existing category tags. The separate tag maintenance tool calls RimWorld's local Steamworks UGC path, using RimWorld app id `294100` and the native Steam API library bundled with the installed RimWorld app. It requires the desktop Steam client session to be logged on from Steamworks' point of view.

On Windows, native-library discovery reads `HKCU\Software\Valve\Steam\SteamPath`
(falling back to `C:\Program Files (x86)\Steam`), then the Steam libraries in
`steamapps\libraryfolders.vdf`. It uses the library containing
`appmanifest_294100.acf` and RimWorld's `steam_api64.dll`, including older Unity
plugin locations. `STEAMWORKS_NATIVE_LIB` still overrides discovery.
Run records use `%LOCALAPPDATA%\SteamWorkshopAgent` on Windows; macOS keeps
`~/Library/Application Support/SteamWorkshopAgent`. The companion uses the same
root and `STEAM_WORKSHOP_AGENT_DATA_ROOT` override. PATH discovery honors
Windows `PATHEXT`.

## Build And Test

```sh
dotnet build SteamWorkshopAgent.slnx -c Release
dotnet test SteamWorkshopAgent.slnx
```

Without game references, run the unit tests and the companion's shared-source
compile check separately:

```sh
dotnet test tests/SteamWorkshopAgent.Tests/SteamWorkshopAgent.Tests.csproj -c Release
dotnet build tests/SharedSources.Net472/SharedSources.Net472.csproj -c Release
```

The compile check reads every linked source from the companion project and
builds it against net472 reference assemblies with nullable analysis and
warnings as errors. It is also included in the solution. It does not load Steam
or require game assemblies; the reference-assemblies package restores from NuGet.

## Local Install

This workspace follows the same local MCP install pattern as the active `gabs` and `decompiler` servers: publish a stable native binary under `~/.codex/mcp-servers/<server-dir>/` and point Codex at that installed binary.

```sh
make install
```

The install target writes:

```text
~/.codex/mcp-servers/steam-workshop-agent/SteamWorkshopAgent
```

Local redeploys should overwrite that binary in place. Do not point MCP config at repo-local build output.

## MCP Client Launch

Codex CLI:

```toml
[mcp_servers.steam-workshop-agent]
command = "/Users/you/.codex/mcp-servers/steam-workshop-agent/SteamWorkshopAgent"
args = ["server"]
```

Generic MCP client:

```json
{
  "command": "/Users/you/.codex/mcp-servers/steam-workshop-agent/SteamWorkshopAgent",
  "args": ["server"]
}
```

Restart the MCP client after changing the installed binary or MCP configuration. Codex loads MCP servers at session startup.

## Tools

`SteamStatus`

Checks whether SteamCMD is installed for emergency use, whether RimWorld is installed through Steam, whether RimWorld's native Steam API library is available for publishing, and where Steam Workshop logs are expected.

`SteamSessionProbe`

Starts a short-lived detached helper process, initializes RimWorld's native Steam API, and reports whether the Steam desktop session authenticated it as app `294100`. It does not read or modify a Workshop item.

`SteamUser.BLoggedOn=false` means the desktop client has no live Steam-server connection; a visible Steam window alone is not proof that the session is usable. If the probe returns `failureCode=desktop-session-offline` and `recoveryAction=restart-steam`, fully quit and reopen Steam, wait for it to reconnect, and probe again before starting RimWorld or selecting a fallback. A common cause is SteamCMD logging into the same account and replacing the desktop session while leaving Steam's UI open.

`RimWorldModInspect`

Reads a RimWorld mod source repository or already-deployed mod folder and returns the metadata needed for Workshop publishing.

`WorkshopReleasePlan`

Creates a dry-run plan for a GitHub release tag. It reads the GitHub release body, resolves mod metadata, prepares SteamCMD VDF content, and reports validation issues before upload.

Steam changenotes are formatted from the mod metadata and GitHub release body by default:

```text
v1.2.3

GitHub release body

GitHub release: https://github.com/owner/repo/releases/tag/v1.2.3.0
```

Pass `changeNote` to use custom Steam changenote text for the plan or publish. The agent still prefixes the version heading unless your text already starts with it.

Four-part mod versions with a trailing `.0` are shortened for the Steam heading only.

`WorkshopPublishRelease`

Publishes a confirmed release to Steam Workshop. `backend=auto` and `backend=standalone` prepare an owner-verified, expiring request, fingerprint the exact staged content, and submit it through a detached Steamworks helper. If desktop Steam does not authenticate that helper, the returned result has `fallbackAllowed=true` and can be submitted once through the RimBridge companion running inside RimWorld. Existing tags are preserved.

`backend=steamcmd` is an explicit emergency path. It is never selected automatically and is the only release path that needs a SteamCMD username or cached SteamCMD credentials.

Do not pre-authenticate or probe SteamCMD before trying the desktop backend. A SteamCMD login to the same account can replace the desktop client's live session. After an explicitly selected SteamCMD fallback completes, probe desktop Steam again and restart it if the fallback displaced its session.

Source release builds always pass `BuildBridgeTools=false` so GitHub/Steam release artifacts contain only the main mod payload. Local validation builds can keep building companion BridgeTools by default.

`WorkshopPublishDeployedRelease`

Publishes an already-built deployed mod folder through the same prepared-request and backend policy. This path does not rebuild the mod and does not refresh Workshop tags. Use it when a separate Release build has already produced the exact deployed folder that should be uploaded.

`WorkshopPublishPrepared`

Submits a still-valid prepared request through the detached helper. The request must live under the agent's run directory and its content fingerprint must still match.
The parent, detached helper and companion refuse a request whose result records
submit intent, submission, an ambiguous outcome or completion. Read back the
Workshop item instead; `verify` remains available. An unreadable result also
fails closed. Only a proven pre-submission failure remains eligible for fallback.
Every standalone helper and companion attempt first creates an exclusive,
per-request `steamworks-request.json.claim` file with its PID and UTC time.
Atomic `CreateNew` prevents competing processes from both reaching validation
and submission. A known, definitive pre-submit failure with
`submissionStarted=false` preserves the backend's `fallbackAllowed` value.
The holder writes and flushes that result to disk before releasing its claim,
so the companion or a later submitter can claim and revalidate the same request.
For example, failed standalone Steam initialization can hand the unchanged
content to the authenticated companion. An ownership failure still returns
`fallbackAllowed=false`.
Submit intent, submitted, ambiguous, completed, validation failure, an exception,
a timeout or an unknown stage keeps the claim. It never expires by age or PID.
An existing claim means refuse and read back the Workshop item.

`WorkshopVerifyAfterPublish`

Reads Steam's public item details and verifies the prepared title and update timestamp. It never retries an upload. A callback timeout after `submissionStarted=true` is deliberately ambiguous and must be verified before any human retry.

`WorkshopCreateNewMod`

Creates a new Steam Workshop item for a local RimWorld mod through SteamCMD. The tool defaults to a dry run. With `confirm=true`, it builds source repositories into a temporary staging directory or uploads an already-built mod folder directly, creates a new private Workshop item by using `publishedfileid` `0`, reads the id SteamCMD writes back into the VDF, submits the `Mod` tag plus supported RimWorld version tags such as `1.6` through Steamworks, and saves the id to `About/PublishedFileId.txt`.

Source `new-mod` builds use the same release-build convention and pass `BuildBridgeTools=false`.

`WorkshopSetTags`

Sets category tags on an existing RimWorld Workshop item. Pass either a numeric Workshop id or a source/deployed mod path with `About/PublishedFileId.txt`. If `tags` is omitted for a mod path, the tool uses `Mod` plus the supported RimWorld versions from `About/About.xml`.

`WorkshopGetDescription`

Reads the current main Steam Workshop item title and description. Pass either a numeric Workshop id or a local mod path with `About/PublishedFileId.txt`. The result includes the raw editable description text, character count, title, update timestamp, visibility, tags returned by Steam, and the public Workshop URL.

`WorkshopUpdateDescription`

Updates the main Steam Workshop item description field. Pass either a numeric Workshop id or a local mod path with `About/PublishedFileId.txt`, plus the complete new description text. This path writes a small VDF containing `appid`, `publishedfileid`, and `description`; it intentionally omits `contentfolder`, `previewfile`, and tags so the upload is metadata-only. Title and changenote are optional and omitted by default.

`WorkshopSetChangeNote`

Submits a changenote update for an existing RimWorld Workshop item. Pass either a numeric Workshop id or a source/deployed mod path with `About/PublishedFileId.txt`, plus the changenote text and `confirm=true`.

This submits a new Steamworks item update with the supplied changenote. It does not edit old historical changenote entries that Steam has already recorded.

The preferred publish path is intentionally conservative:

1. Refuse to publish from a dirty git worktree.
2. For release publishes, create a detached temporary Git worktree at the requested tag and build that source tree in Release mode into a temporary staging directory with `BuildBridgeTools=false`. This keeps the caller's checkout clean even when the mod build rewrites tracked local deploy artifacts.
3. Validate the staged mod content and preview image.
4. Write `plan.json` plus owner-only `steamworks-request.json` under `~/Library/Application Support/SteamWorkshopAgent/runs/...`.
5. Record the current Workshop creator id and a deterministic digest of every staged content path and byte.
6. Start a detached helper session with `setsid()`, verify app id and Steam account ownership, and apply title, optional description, preview, and content before `SubmitItemUpdate`.
7. Persist a no-fallback `submit-intent` marker before calling `SubmitItemUpdate`, then a `submitted` marker after receiving a valid API call handle. This survives helper crashes and terminal disconnects.
8. Allow fallback only for a known, definitive pre-submit failure after its result is flushed and the holder releases its claim. An intent marker or any possible submission blocks fallback; a timeout or exception never releases the claim.
9. Persist `steamworks-result.json` with owner-only permissions and verify public Workshop state.

The release tag must exist locally so Git can create the detached worktree. If the agent reports that the tag cannot be resolved, run `git fetch --tags` in the mod repository and retry.

The publish tool defaults to dry-run behavior unless `confirm` is `true`.

## Local Smoke Tests

The installed binary also has a thin CLI over the same services used by the MCP tools:

```sh
~/.codex/mcp-servers/steam-workshop-agent/SteamWorkshopAgent status
~/.codex/mcp-servers/steam-workshop-agent/SteamWorkshopAgent inspect /path/to/mod/repo-or-folder
~/.codex/mcp-servers/steam-workshop-agent/SteamWorkshopAgent plan /path/to/mod/repo v1.2.3
~/.codex/mcp-servers/steam-workshop-agent/SteamWorkshopAgent plan /path/to/mod/repo v1.2.3 --changenote "Simple public release note"
~/.codex/mcp-servers/steam-workshop-agent/SteamWorkshopAgent steam-session-probe
~/.codex/mcp-servers/steam-workshop-agent/SteamWorkshopAgent publish /path/to/mod/repo v1.2.3 --confirm --backend auto
~/.codex/mcp-servers/steam-workshop-agent/SteamWorkshopAgent publish /path/to/mod/repo v1.2.3 --confirm --backend steamcmd --steam-user your_steam_username --changenote "Simple public release note"
~/.codex/mcp-servers/steam-workshop-agent/SteamWorkshopAgent publish-deployed /path/to/mod/repo v1.2.3 /path/to/deployed/mod --confirm --backend auto
~/.codex/mcp-servers/steam-workshop-agent/SteamWorkshopAgent verify '/path/to/run/steamworks-request.json' --wait-seconds 90
~/.codex/mcp-servers/steam-workshop-agent/SteamWorkshopAgent new-mod /path/to/mod/repo-or-folder --confirm --steam-user your_steam_username
~/.codex/mcp-servers/steam-workshop-agent/SteamWorkshopAgent description-get /path/to/mod/repo-or-folder
~/.codex/mcp-servers/steam-workshop-agent/SteamWorkshopAgent description-get 928376710
~/.codex/mcp-servers/steam-workshop-agent/SteamWorkshopAgent description /path/to/mod/repo-or-folder description.txt --confirm --steam-user your_steam_username
~/.codex/mcp-servers/steam-workshop-agent/SteamWorkshopAgent description 928376710 description.txt --confirm --steam-user your_steam_username
~/.codex/mcp-servers/steam-workshop-agent/SteamWorkshopAgent set-tags /path/to/mod/repo-or-folder --confirm
~/.codex/mcp-servers/steam-workshop-agent/SteamWorkshopAgent set-tags 3727949765 Mod 1.6 --confirm
~/.codex/mcp-servers/steam-workshop-agent/SteamWorkshopAgent set-changenote 3727949765 --changenote "Simple public release note" --confirm
```

## Current Limits

The agent focuses on initial item creation, release publishing, single-item description updates, and category tag updates. Bulk Workshop description edits can be driven by a manifest or shell loop around the metadata-only `description` command.

The new-mod path creates the initial item with private visibility by default. Pass `--visibility public`, `friends`, or `unlisted` only when that is intentional.

SteamCMD remains available only as the explicitly selected `steamcmd` backend. It is not an automatic fallback because doing so would reintroduce independent credentials and make duplicate-submission handling harder.

Existing Workshop updates preserve tags. Steamworks tag submission remains available as a separate maintenance tool and for first-time items, but release publishing does not refresh tags.

The tool only updates the Workshop description when `updateDescription` is explicitly `true`. By default it publishes the release changenote and leaves the long Workshop page description alone.
