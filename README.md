# EncounterScope

EncounterScope is a Dalamud plugin that records privacy-safe encounter data from FFXIV duties as
JSON Lines. It records every pull in a duty session, including wipes and recommences, so the data
can be inspected and analyzed later.

Logging is enabled by default. Open the settings window from the Dalamud plugin list or with
`/encounterscope`. The window shows hook availability, duty and capture state, event loss counters,
the active path, and a button to open the capture directory.

## Commands

- `/encounterscope` or `/encounterscope config` opens settings.
- `/encounterscope on` enables automatic duty logging.
- `/encounterscope off` disables logging and closes the current session cleanly.
- `/encounterscope status` prints hook, duty, session, storage, and event-loss state.
- `/encounterscope folder` opens the capture directory.

## Capture files

Captures are stored in the plugin configuration directory under `captures`. A logging session starts
when the client is bound by duty and logging is enabled, and ends on duty exit, disablement, writer
failure, or plugin unload. Loading or enabling mid-duty starts an observed-mid-duty session. Combat
IDs and monotonic combat clocks restart for each pull.

Files use the name
`encounterscope_<UTC>_<32-hex-session>_partNNN.jsonl`. Active files end in `.partial`; interrupted
or failed files become `.incomplete.jsonl`. Files rotate around 100 MB, and retention is limited to
3 GB of EncounterScope-owned completed sessions. Files owned by other applications are never managed.

The JSONL contract uses schema version 3, with no version 1 or 2 output or migration path. Records include
session and combat timing, territory and Content Finder context, cast starts and terminal states,
processed action resolutions, status lifecycle changes, duty and combat lifecycle markers, health
records, and segment boundaries.

Every observed cast occurrence receives a session-local `castObservationId` carried from
`cast_started` to `cast_completed` or `cast_cancelled`. Completion requires elapsed-timer evidence
or an exact same-framework-frame source/action match with `action_resolved`; other observed endings
record a cancellation reason. `cast_interrupted` is reserved for a future authoritative signal and
is not inferred merely because an interruptible cast stopped.

Cast scanning is limited to encounter Battle NPCs. Processed actions from known players and
player-owned pets are omitted; actions from unresolved sources remain available because hidden
boss/helper actors may not be represented in the live object table.

Status scanning covers encounter Battle NPCs, party/alliance characters exposed through Dalamud's
party list, and battle-character pets owned by those members. Each status occurrence receives a
session-local `statusObservationId`. Ordinary duration countdown is deduplicated; refreshes,
parameter/stack changes, source changes, and removals produce lifecycle records. A removal is called
`natural_expiration` only when observed within 0.5 seconds of the predicted expiration. Early
disappearance remains `removed`, because snapshot polling cannot prove a cleanse.
Scripted transformations can temporarily make an actor's native status manager unavailable. The
actor remains tracked and only that frame's status snapshot is skipped until the manager returns.

## Privacy and limitations

Player-character names are never read into capture models or written to disk. Actor references use
numeric IDs, kind, class/job, position, rotation, and fixed-English NPC labels when reliably
available. Action identity is always numeric action type plus ID; fixed-English action names are
optional labels. Raw per-target effect slots are not captured.

The processed-action hook sees only casters available to the local client, and framework scanning
can miss extremely short casts. Some scripted states temporarily expose actors without native cast
information; those actors remain available for resolution metadata and their cast snapshot is
skipped safely until cast information returns.

See [Capture Analysis Handbook](docs/capture-analysis.md) for reconstructing sessions and pulls,
correlating casts with resolutions, comparing action and actor identities, interpreting timing and
geometry, checking capture completeness, and reporting confidence.

See [Data-Capture Roadmap](TODO.md) for proposed status, tether, marker, actor lifecycle, cast
lifecycle, world-state, outcome, visual-effect, environmental, movement, and analysis additions.

See `AGENTS.md` for architecture, invariants, and verification requirements.
