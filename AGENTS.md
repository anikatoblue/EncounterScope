# EncounterScope Repository Guide

Follow the parent FFXIV workspace guide, including its required Git identity and signed-commit
checks.

## Purpose and scope

EncounterScope 0.1 is a .NET 10 / Dalamud API 15 duty encounter logger. It records privacy-safe
JSONL data for later analysis. Logging defaults to enabled and is limited to duties.

## Compatibility policy

New or changed functionality must not include backward compatibility by default. Compatibility,
migration, fallback, legacy paths, and legacy-format support require a conscious decision from the
prompting user. Surface the tradeoff and ask the user instead of inferring that compatibility is
desired. Any future continuity for capture schemas, configuration, or storage paths must therefore
be explicitly requested.

## Layout and data flow

- `src/EncounterScope.Core` contains clocks, event contracts, duty and combat lifecycle state,
  cast tracking, bounded queues, JSONL writing, recovery, rotation, and retention.
- `src/EncounterScope` contains `Plugin`, Dalamud adapters, the processed-action detour, actor and
  action metadata resolution, configuration, commands, and the settings/status window.
- `tests/EncounterScope.Core.Tests` is a dependency-free executable test harness.
- `docs/capture-analysis.md` is the analysis handbook for JSONL lifecycle reconstruction, action
  and actor identity, cast/resolution correlation, timing, geometry, confidence, and completeness.
- `TODO.md` is the prioritized data-capture roadmap. Implemented roadmap items must update their
  checklist state and all affected contract, architecture, configuration, test, and analysis docs.

A session starts while any `BoundByDuty`, `BoundByDuty56`, or `BoundByDuty95` flag is active,
logging is enabled, and the action-effect hook is available. A session ends when all duty flags
clear, logging is disabled, the writer fails, or the plugin unloads. Loading or enabling mid-duty
starts an observed-mid-duty session. Every `InCombat` false-to-true transition creates a sequential
combat ID with a new monotonic clock; wipes and recommences stay in the same duty session.

The framework thread indexes visible objects and scans casts only during an active session. It
tracks battle-actor presence separately from available cast snapshots, reads native cast
information once, and skips only the cast snapshot when `GetCastInfo()` is null during transient
scripted states. Newly visible casts already in progress are recorded with `observedMidCast=true`.
Only encounter Battle NPCs are cast-scanned; known player actors and player-owned pets are excluded.
Processed actions from known players and player-owned pets are also excluded, while unresolved
sources remain captured because hidden encounter helpers may not be present in the object table.
Processed actions are normalized before cast transitions in the same framework update so exact
source and numeric action identity can provide completion evidence without delaying events.

The `ActionEffectHandler.Receive` detour snapshots the already-unscrambled action identity, header,
target IDs, and observation-time capture context into a bounded queue. It must call the original
exactly once in `finally`; exceptions must not escape. Never perform metadata lookup, file I/O,
waiting, UI work, or unbounded work in the detour. The framework thread resolves metadata and sends
normalized events to one background writer.

## Capture contract and privacy

JSONL uses `schemaVersion: 2`; version 1 output and migration are not supported. Supported record
types are `session_start`, `segment_start`, `duty_marker`, `territory_changed`, `combat_started`,
`combat_ended`, `cast_started`, `cast_completed`, `cast_cancelled`, `cast_interrupted`,
`action_resolved`, `health`, `segment_end`, and `session_end`. Common fields include session ID,
serialized sequence, observation-time UTC, monotonic session/combat seconds, combat ID, territory,
Content Finder condition, and a camel-case payload.

Numeric action type and ID are canonical. Names are optional fixed-English labels and never define
identity. Player-character names must never be read into capture models, logs, or diagnostics.
Actor references may contain hexadecimal object/entity IDs, data ID, kind, class/job, position,
rotation, and reliably resolved fixed-English NPC labels. Leave a label null when safe metadata is
unavailable. Raw eight-slot target effects remain excluded.

Capture interpretation must preserve numeric `(action type, action ID)` identity, separate cast
starts, cast terminals, and action resolutions, account for simultaneous helper actors, and check
health/drop records before treating an absent event as evidence. Each cast occurrence has a
session-local `castObservationId`. Completion requires timer or matching same-framework-frame
resolution evidence; other observed endings are cancellations with a reason. `cast_interrupted` is
reserved for authoritative future evidence and must not be inferred from an interruptible cast
ending. Follow `docs/capture-analysis.md` for the complete analysis and reporting methodology.

Use `Stopwatch.GetTimestamp()` or `IEventClock` for relative time. Capture event timestamps and
combat context when observation occurs, not when a queue is drained.

## Storage and failures

Raw and writer queues are bounded at 16,384 and drop rather than block. Losses are reported through
health records and `/encounterscope status`. Files live under `captures` and use
`encounterscope_<UTC>_<session>_partNNN.jsonl.partial`; clean files lose `.partial`, and interrupted
files become `.incomplete.jsonl`. The writer flushes after 256 records or one second, rotates near
100 MB, and caps managed storage at 3 GB.

Retention may delete only completed EncounterScope files, oldest session group first. It must never
delete an active group or files owned by other applications. Startup recovery renames stale
EncounterScope partials as incomplete. Capacity exhaustion or writer failure disables logging,
reports once, and preserves an incomplete segment. Hook initialization failure leaves the plugin
loaded but logging unavailable.

## Configuration and commands

Configuration version 1 contains only `Enabled`, defaulting to true. Do not read or migrate
configuration or captures owned by other applications.

- `/encounterscope` and `/encounterscope config` open settings.
- `/encounterscope on` and `off` control automatic duty logging.
- `/encounterscope status` reports hook, duty, writer, queue, and path state.
- `/encounterscope folder` opens the capture directory.

## Build and verification

From the repository root:

```powershell
dotnet restore EncounterScope.slnx --locked-mode
dotnet build EncounterScope.slnx -c Debug --no-restore
dotnet run --project tests\EncounterScope.Core.Tests\EncounterScope.Core.Tests.csproj -c Debug --no-build
dotnet format EncounterScope.slnx --verify-no-changes --no-restore
```

Automated coverage must include duty lifecycle, monotonic clocks, multiple pulls, mid-cast
discovery, bounded drops, JSONL shape and privacy, rotation, recovery, retention isolation, and
capacity failure. In-game verification must cover settings and commands, enablement mid-duty,
casted and instant actions, multiple targets, two pulls and a wipe, duty completion/exit, clean
unload, unavailable hook behavior, writer failure, and actors whose native cast info is temporarily
null.

Before committing, verify `user.name`, `user.email`, `user.signingkey`, `gpg.format`, and
`commit.gpgsign` from this repository as required by the parent guide. Any architectural,
behavioral, schema, command, configuration, retention, privacy, hook, UI, build, or test change must
update this guide in the same change.
