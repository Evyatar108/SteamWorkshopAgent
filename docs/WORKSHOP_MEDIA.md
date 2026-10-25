# Gallery and visibility commands

These CLI-only commands change page metadata, not mod content, the main preview,
description or tags. Keeping them separate from `publish-folder` makes deletion
and going public separate approvals.

Before first use, get the owner's approval for the lookup and upload. No live
Steam call was used to develop these commands.

Configure the native helper's JSON argv array. Paths below are examples, not installed defaults.

```powershell
$env:STEAM_WORKSHOP_AGENT_MEDIA_COMMAND = '["python","D:\\toolkit\\scripts\\steam_common.py","run"]'
SteamWorkshopAgent gallery 123456 D:\mod-media\gallery
SteamWorkshopAgent gallery 123456 D:\mod-media\gallery --confirm --expect-digest <approved-digest>
SteamWorkshopAgent visibility 123456 public
SteamWorkshopAgent visibility 123456 public --confirm --expect-digest <approved-digest>
```

Both plan and confirm make native full-state lookups. Each helper goes through
`run`, which holds the machine-wide throttle lock throughout the helper. Before
each outbound query **and** submission, the helper requests a grant through a
stderr/stdin handshake. The wrapper waits, records that request durably, then
grants it. Query and submit are therefore two separately spaced/countable
requests even in one helper. Never point the wrapper at a command that acquires
the throttle again. Query results bypass the local cache. Query timeout is 30
seconds, submission timeout is 90 seconds and the outer helper timeout is three
minutes. Existing content-publishing commands still use the separate
`STEAM_WORKSHOP_AGENT_DETAILS_COMMAND` configuration; media commands no longer
need a public-details lookup.

Run `workshop_media.py check` before planning. The gallery folder contains only
1 to 10 static PNG/JPG images, lowercase ASCII names, each under 1,000,000 bytes.
Those count and dimension rules are house policy, not claimed Steam limits.
The uploader checks names, size and file signatures, not full image decoding.
Every matching original filename is replaced because Steam returns no content hash.
Unmatched existing images are removed; videos and other preview types stay.
An empty folder is refused, so this command cannot remove every image.

The digest binds the item, creator, current visibility, every remote preview
index/type/name/URL, local filenames and SHA-256 values, and the exact actions.
URLs are kept in the internal binding, not repeated in output. Confirmation
rebuilds the plan and refuses any change. Immediately before `StartItemUpdate`,
the helper's query returns the full current item/creator/visibility/preview
state, and the helper rebuilds the digest from that state, not the saved
`Before`. A changed visibility or creator refuses even when previews are
identical. Run-owned immutable snapshots prevent a
later edit to the gallery folder from changing the submitted bytes. Replacements
run before descending-index removals, then additions.

The request lives in the existing request store's runs directory at
`media-<digest>\steamworks-request.json`. A filesystem claim, the shared replay
guard, a persisted submit-intent marker and a completion result prevent replay.
Preparation uses an isolated directory and atomic rename; interruption before
rename leaves no canonical request. Expired requests and any claimed request
fail closed, including setter failures and uncertain uploads. Claims never
expire automatically. Read back the page and inspect its run record before an
operator clears or replaces a failed request. There is no automatic fallback.
Internal local refusals use exit code 4, which the throttle wrapper preserves
without starting a Steam failure cooldown. A refusal before the first request
records no request; a changed-state refusal after a successful query keeps that
query's count/time but does not mark it as a failed Steam request. A network
failure or uncertain submission after a grant still starts the failure cooldown.

Visibility is a separate command, supporting public, friends, private and unlisted.
It does not prove the page has suitable graphics. Keep new pages private until
the owner has seen the page with the chosen gallery and explicitly approves
making that item public.

## Agent-visible output contract

`WorkshopMediaTests.Output_Key_Snapshot_And_Overhead_Bounds` checks keys both ways.

| Key | Reader |
| --- | --- |
| `status` | CLI consumer distinguishes success/refusal |
| `data.digest` | owner approves; next confirm sends `--expect-digest` |
| `data.actions[].kind` | owner reviews add/replace/remove/visibility |
| `data.actions[].name` | owner identifies affected image or visibility transition |
| `data.actions[].index` optional | owner reviews the exact remote index being replaced/removed |
| `data.status` | agent knows plan versus submitted and the next required read-back |
| `message`, `details` on error | existing CLI error contract; agent recovery and error class |

Output omits internal snapshots, URLs, creator IDs and repeated target IDs.
Tests bound the sample receipt to 700 characters and retain the full approval
digest. The digest is an exact safety binding, not an incidental opaque handle.
