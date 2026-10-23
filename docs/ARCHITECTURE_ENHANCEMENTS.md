# Architecture Enhancements

SteamWorkshopAgent treats Steam Workshop automation as a hybrid system rather than a single publishing backend.

## Emergency SteamCMD Backend

The emergency implementation uses SteamCMD for content upload:

```text
steamcmd +login <user> +workshop_build_item <workshop.vdf> +quit
```

This is useful because it is simple, scriptable, and officially documented for Workshop item creation and updates. It can publish content, preview image, title, description, visibility, and changenote through the generated VDF.

Its main weaknesses are:

- It uses SteamCMD's own login token cache, not the already-authenticated Steam desktop client session.
- It is poor at reading current Workshop state.
- It has no natural structured read path for page stats, current tags, current previews, current rendered description, or previous changenotes.
- Tag handling through VDF is less explicit than direct `ISteamUGC.SetItemTags`, so the generated update VDF preserves existing tags.

Keep SteamCMD as an explicit manual fallback. Do not select it automatically and do not run credential prechecks during normal releases.

## Steamworks Tag Backend

RimWorld's own Workshop uploader does not use SteamCMD. Decompilation of the current RimWorld `Assembly-CSharp.dll` shows it uses Steamworks UGC calls from inside the running game process:

```text
SteamUGC.CreateItem(...)
SteamUGC.StartItemUpdate(...)
SteamUGC.SetItemTitle(...)
SteamUGC.SetItemDescription(...)
SteamUGC.SetItemPreview(...)
SteamUGC.SetItemTags(...)
SteamUGC.SetItemContent(...)
SteamUGC.SubmitItemUpdate(...)
```

This means RimWorld inherits the logged-in Steam desktop client session, which is why it normally does not ask for Steam credentials.

The current tag backend is a small local Steamworks helper that:

- initializes Steamworks for RimWorld app id `294100`;
- uses the active Steam client session instead of SteamCMD credentials;
- uses `ISteamUGC.StartItemUpdate`, `ISteamUGC.SetItemTags`, and `ISteamUGC.SubmitItemUpdate`;
- runs in a child helper process so native Steamworks stdout cannot corrupt stdio MCP traffic;
- returns detailed Steam result codes, logged-on state, timeout state, and whether the user needs to accept the Workshop legal agreement.

The tag-only backend is used by:

- `WorkshopSetTags` for existing items;
- `WorkshopCreateNewMod` after SteamCMD returns the new `publishedfileid`;

The correct RimWorld mod tag set is `Mod` plus the supported game versions from `About/About.xml`, for example `Mod` and `1.6`.

The detached full-publisher path has been validated against the authenticated desktop Steam session on the current macOS setup. If a future session reports `SteamUser.BLoggedOn=false` with the expected Steam ID and app ID, the agent reports `failureCode=desktop-session-offline` and `recoveryAction=restart-steam`. The guided workflow must fully quit and reopen Steam, wait for it to reconnect, and reprobe before attempting the in-game companion. This specifically covers SteamCMD replacing the desktop account session while leaving Steam's UI visible. The agent does not select SteamCMD automatically.

## Steamworks Publisher Backend

The preferred full publish backend extends the local Steamworks helper so it:

- accepts staged content, preview path, title, description policy, visibility, and changenote from the MCP server;
- uses `ISteamUGC.StartItemUpdate` / `SubmitItemUpdate`;
- pumps Steam callbacks until completion;
- reports upload progress through `GetItemUpdateProgress`;
- returns detailed Steam result codes.

Implemented safeguards include:

- owner-only, expiring request and result files;
- exact Workshop creator-account verification before item mutation;
- a deterministic content digest checked again in the publishing process;
- `setsid()` isolation so native Steam calls cannot suspend the Codex terminal process group;
- a strict pre-submit fallback boundary;
- upload progress and structured callback results;
- public item-details verification without automatic retries.

Advantages:

- No separate SteamCMD login setup when Steam is already authenticated.
- Keeps existing item tags unchanged during routine releases.
- Cleaner support for key-value tags and metadata through `AddItemKeyValueTag`, `RemoveItemKeyValueTags`, and `SetItemMetadata`.
- Cleaner support for additional previews and videos through the `AddItemPreview*`, `UpdateItemPreview*`, and `RemoveItemPreview` calls.
- Better parity with RimWorld's built-in Workshop behavior.

Costs:

- More implementation work than SteamCMD.
- Needs Steamworks redistributables and careful app id setup.
- Needs reliable callback handling, timeout behavior, and result normalization.
- Needs continued testing around wrong accounts, legal agreement required, app license missing, and invalid Workshop ownership.

## RimWorld Session Fallback

Steam may initialize a standalone helper without authenticating it as a logged-on user. In that pre-submit state, use the companion DLL in the running RimWorld process. RimWorld owns Steam initialization and callback pumping; the companion only invokes `ISteamUGC` on the main thread.

Both submitters acquire the same per-request exclusive claim before validation.
A known pre-submit failure with `success=false`, `submissionStarted=false` and
`outcomeDefinitive=true` can hand the request back. The holder first persists and
flushes the final result, preserving the backend's `fallbackAllowed` value, then
releases its claim. The next submitter acquires a new claim and revalidates the
receipt and content. For example, a standalone `steam-init` failure can fall
back to the companion without rebuilding or changing the approved content.

Claims remain after submit intent, submission, ambiguity, completion, an
exception, a timeout or a failed result write. A crash after writing a pre-submit
receipt but before hand-back also leaves the claim in place. Never reclaim a
claim by age or PID; refuse and read back the item instead.

The GABS profile `rimworld-workshop-headless` uses an isolated save-data directory and only Core, Harmony, and RimBridgeServer. Its isolated `Config/Prefs.xml` sets `volumeMaster` to `0`, so background publishing cannot play game audio or change the player's normal sound settings. It uses `-batchmode` without `-nographics`: live validation showed that `-nographics` connects the bridge but triggers a continuous RimWorld texture-atlas exception loop. A normal visible RimWorld launch is the final Steamworks fallback. SteamCMD is separate and manual.

## Read And Stats Backend

Publishing and reading should be separated.

For current Workshop state, use public Steam Web APIs and targeted page reads before reaching for either SteamCMD or Steamworks.

Candidate read tools:

- `workshop_item_details`: fetch structured published-file details by Workshop id.
- `workshop_page_snapshot`: fetch the human-visible Workshop page and extract rendered title, description, tags, preview URLs, and visible counters where available.
- `workshop_compare_release_to_workshop`: compare a GitHub release plan against current Workshop state.
- `workshop_verify_after_publish`: confirm that last-updated fields, title, visible description policy, and expected metadata changed after an upload.

Useful Steam Web API surfaces:

- `ISteamRemoteStorage.GetPublishedFileDetails` for basic item metadata by `publishedfileid`.
- `IPublishedFileService.QueryFiles` for richer query results, including options for vote data, tags, key-value tags, previews, children, metadata, and playtime stats.

Some `IPublishedFileService` methods require a Steamworks publisher key and must be treated as sensitive/server-side operations. Examples include developer metadata, tags, ban status, and incompatible status updates.

## Changenote History Caveat

Both SteamCMD and Steamworks can submit a changenote, but neither appears to provide a clean, stable public API for reading the full prior changenote history of a Workshop item.

Treat changenote-history reads as best-effort:

- try public Workshop page parsing first;
- avoid depending on Steam page markup for critical publish correctness;
- record submitted changenotes locally in `~/Library/Application Support/SteamWorkshopAgent/runs/.../plan.json`;
- compare future GitHub release notes against locally recorded submissions when available.

## Recommended Direction

The implemented architecture is:

1. Keep SteamCMD as a documented fallback publisher.
2. Use non-mutating Workshop read tools using the public Web API.
3. Use the full Steamworks helper as the preferred local publisher.
4. Use read tools before and after publishing to validate expected Workshop state.
5. Use RimWorld's authenticated session only when the standalone helper fails before submission.
6. Keep destructive or sensitive operations behind explicit confirmation flags.

The target operator experience should be:

```text
Plan:
  read local mod metadata
  read GitHub release
  read current Workshop state
  show exact differences

Publish:
  build release content
  upload through preferred backend
  submit changenote
  preserve or update description/tags according to explicit policy

Verify:
  read Workshop state again
  report whether the public page matches the plan
  persist local publish evidence
```

## References

- Steam Workshop implementation guide: https://partner.steamgames.com/doc/features/workshop/implementation
- `ISteamUGC` API: https://partner.steamgames.com/doc/api/ISteamUGC
- `ISteamRemoteStorage.GetPublishedFileDetails`: https://partner.steamgames.com/doc/webapi/ISteamRemoteStorage
- `IPublishedFileService.QueryFiles`: https://partner.steamgames.com/doc/webapi/IPublishedFileService
