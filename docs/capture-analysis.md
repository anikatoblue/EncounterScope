# Capture Analysis Handbook

This guide describes how to turn EncounterScope JSONL captures into defensible encounter findings.
It covers the capture contract, practical extraction, event correlation, common traps, geometry,
cross-pull comparison, and confidence rules.

## Start with the evidence boundary

The capture records two complementary observation points:

- `cast_started` is a visible actor beginning a cast. It is normally the best source for an early
  warning, cast duration, target, and interruptibility.
- `action_resolved` is the processed action-effect event. It covers cast completion, instant
  actions, helper actions, multi-target resolutions, and actions performed by actors whose cast was
  not visible.

Neither stream is a complete combat protocol. Captures intentionally omit raw per-target effect
slots and player names. They do not directly record statuses, tethers, icons, VFX paths, object
spawn/despawn packets, model states, or authoritative AoE shapes. Loaded-object visibility limits
what can be observed. Treat conclusions about unrecorded state as hypotheses requiring another
source or an in-game check.

## JSONL structure

Each line is one independent JSON object. Parse line by line; do not treat a file as a JSON array.
Every record has this envelope:

| Field | Meaning |
|---|---|
| `schemaVersion` | Contract version for the entire record. Validate before interpreting payloads. |
| `recordType` | Lifecycle, cast, resolution, health, or segment event. |
| `sessionId` | Stable ID shared by all segments from one duty capture session. |
| `sequence` | Monotonically increasing serialization order within the session. It may have gaps. |
| `timestampUtc` | Observation-time UTC in round-trip ISO-8601 format. |
| `sessionElapsedSeconds` | Monotonic seconds since this capture session began. |
| `combatElapsedSeconds` | Monotonic seconds since the current combat interval began, or `null`. |
| `combatId` | Sequential combat interval within the session, or `null` outside combat. |
| `territoryId` | Territory at observation time. |
| `contentFinderConditionId` | Duty context when known. |
| `payload` | Record-type-specific data. |

Relative values are observation-time snapshots from a monotonic clock. Use them for mechanic
intervals. Wall-clock UTC is useful for relating the capture to external material but can be
affected by system-clock correction.

JSON numbers always use `.` as their decimal separator. PowerShell may display them with `,` under
a locale such as Swedish; that is display formatting, not different file data.

### Ordering across segments

Files rotate near the configured segment limit. Reconstruct a session by selecting files with the
same `sessionId`, parsing all lines, and sorting by `sequence`. Do not concatenate by filesystem
modification time.

Sequence numbers need not be contiguous. Rotation writes boundary records while an event is being
placed in the next segment, so unused sequence values are possible. Require strict increase, not
`previous + 1`.

`segment_start` can be the first serialized record and can precede `session_start`. Lifecycle
analysis must search by `recordType`, not assume a fixed line number. Observation timestamps can
also be slightly different from serialization order because events are stamped before queues are
drained. For close events, use monotonic observation time and use `sequence` as a stable tie-breaker.

Active `.partial` and recovered `.incomplete.jsonl` files can still contain useful evidence, but
their final records may be absent. State that the session was incomplete and avoid conclusions that
depend on a clean ending.

## Reconstruct duty and combat lifecycle first

Before inspecting actions, list:

- `session_start` and `session_end`
- `duty_marker`
- `territory_changed`
- `combat_started` and `combat_ended`
- `health`
- `segment_start` and `segment_end`

A session is the duty-level container. It persists across pulls, wipes, recommences, hallway
combat, and bosses. A `combatId` is only an `InCombat` false-to-true interval; it is not inherently a
boss pull. Short intervals may be trash, traps, or isolated adds.

Identify an encounter interval using multiple signals:

1. Expected territory and Content Finder condition.
2. Presence of the boss NPC data ID or distinctive action IDs.
3. Plausible duration and timeline.
4. A clean combat end or duty marker when available.

`observedMidCombat=true` means the combat clock began when observation began, not at the real pull
start. Absolute mechanic times in that interval are offset, but intervals between subsequently
observed events remain useful.

## Numeric action identity is canonical

An action reference contains:

```text
(action.typeId, action.id)
```

Use that pair for matching and grouping. `action.name` and `typeName` are informational fixed-English
labels. Never merge by name because:

- different IDs can share a label;
- visually different boss variants commonly reuse names;
- helper and primary actors can use separately identified actions with the same name;
- an unknown or incompatible action type can have a null label;
- labels can change while numeric identity remains the useful evidence.

Retain unknown IDs. A null name means “unresolved label,” not “invalid event.” Include the action
type in exports so IDs from different action namespaces are not accidentally compared.

The header also contains a `spellId`. Preserve it for low-level comparison, but use
`payload.action.id` with `typeId` as the normalized identity.

## Actor identity and privacy

Actor references can include:

- `gameObjectId`: hexadecimal 64-bit object reference;
- `entityId`: hexadecimal 32-bit instance reference when resolved;
- `dataId`: actor template/base identity when resolved;
- `objectKind` and `classJobId`;
- position and rotation;
- `npcName` for reliably resolved NPCs.

Use IDs at different scopes:

| Question | Useful identity |
|---|---|
| “Which NPC kind performs this?” | `dataId`, supported by `npcName` |
| “Which of several identical adds acted first?” | `entityId` or `gameObjectId` within that pull |
| “Is this the same physical instance later?” | `gameObjectId`/`entityId` within the session |
| “Does this repeat across pulls?” | action identity plus NPC `dataId`, duty, and mechanic context |

Object/entity IDs are ephemeral. Do not carry their literal values into reusable encounter rules.
The ordering or allocation pattern of IDs can be investigated, but it is patch-sensitive and needs
multiple independent samples.

Some hidden controller actors use generic-looking data IDs or names also seen elsewhere. Do not
interpret `dataId` in isolation; combine it with duty, combat, timing, action identity, and nearby
primary-boss activity.

Player actor references deliberately have `npcName: null`. Do not try to reconstruct player names
from another source when producing privacy-safe encounter artifacts.

## Reading `cast_started`

The payload contains:

- normalized action identity;
- source and optional target snapshots;
- `currentCastSeconds`, `baseCastSeconds`, and `totalCastSeconds`;
- `interruptible`;
- `observedMidCast`.

For normal observed starts, `currentCastSeconds` should be near zero. `baseCastSeconds` is the best
available nominal warning duration. Use `totalCastSeconds` when checking unusual cast-state
behavior, but do not assume either duration equals the damage snapshot delay without correlating a
resolution.

Ignore `observedMidCast=true` when deriving an exact cast-start timeline or advance-warning timer.
It means the actor was first observed after the cast had already begun. The action identity is still
useful, and the remaining cast can still be correlated with its resolution.

A cast can be absent because it was instant, too short for a framework scan, performed while the
source was not loaded, or exposed during a transient native state without cast information. Absence
of `cast_started` does not imply absence of an action.

## Reading `action_resolved`

The normalized action and source are accompanied by a processed header and a list of target actor
references. Important header fields include:

- `animationTargetId`
- `globalSequence` and `sourceSequence`
- `animationLockSeconds`
- `rotationInt` and normalized `rotationRadians`
- `spellId`
- `animationVariation`
- `flags`, `showInLog`, and `forceAnimationLock`
- `targetCount`
- `targetPosition`

The `targets` list identifies affected object IDs and supplies metadata when those objects were
loaded. It does not contain raw damage, healing, statuses, knockback, or the eight effect slots.

An `action_resolved` without a matching visible cast is valuable. It may be:

- an instant mechanic;
- a hidden/helper actor action;
- a repeated pulse;
- a per-target or per-platform resolution;
- the damage event belonging to a differently named high-level cast.

Do not automatically create warnings from every resolution-only ID. First determine whether it is
an early, stable observation point or merely damage arriving after the player already needed to
react.

## Correlating casts and resolutions

Correlation is evidence-based rather than a guaranteed foreign key. Start with:

1. Same combat and mechanic window.
2. Same `(typeId, actionId)` when the cast resolves under its own ID.
3. Same source instance, or a stable primary/helper actor relationship.
4. Resolution time near `cast start + nominal duration`.
5. Repetition of the relationship across occurrences and pulls.

Boss mechanics often use a high-level cast followed by different helper IDs. Group events into a
short time window and inspect all non-player sources instead of searching only for an identical
action ID.

For each occurrence, record at least:

```text
combatId, mechanic ordinal, cast ID, cast start, duration,
source data/entity ID, helper IDs, resolution times, target count,
positions, rotations, and uncertainty notes
```

## Avoid false duplicate counts

One mechanic can produce many superficially duplicate lines:

- several cloned helper actors cast simultaneously;
- a single action has multiple targets;
- each platform or lane has its own controller;
- pulses repeat under one action ID;
- several actors share one NPC label and data ID.

Count both event rows and distinct source instances. Grouping solely by action ID can turn “six
helpers resolved once” into “the boss cast six times.”

Useful occurrence grouping keys are contextual, for example:

```text
(combatId, action identity, rounded observation window, distinct source entity ID)
```

Do not round too aggressively when mechanics pulse quickly. Inspect the raw monotonic times before
choosing a grouping window.

Repeated callbacks from the same source can sometimes be recognized by identical or near-identical
time, action identity, sequences, animation target, and source instance. Preserve raw rows and make
deduplication an analysis view rather than destructive preprocessing.

## Timing analysis

Use `combatElapsedSeconds` within a pull and `sessionElapsedSeconds` across the duty. Recommended
timing products include:

- ordered boss-cast timeline;
- cast-to-resolution delay;
- interval between waves or helpers;
- repeated mechanic cycle length;
- pull-to-pull alignment after selecting a common anchor cast;
- earliest reliable observation point for a warning.

Mechanic timing should be reported with realistic precision. Millisecond serialization does not
make framework observation millisecond-accurate. Hundredths of a second are useful for correlation;
tenths are usually more honest for player-facing timers.

When comparing pulls, align on a distinctive cast rather than assuming combat zero represents the
same moment. Mid-combat startup and brief condition changes can shift or split combat clocks.

## Position, rotation, and geometry

Positions are world-space `(x, y, z)`. FFXIV arena-plane work normally uses `(x, z)`; `y` is
elevation. Determine the arena center empirically from boss, helper, or repeated symmetric
positions. Never reuse a center from another arena merely because the coordinates look familiar.

`rotationRadians` is useful for ordering and relative-angle analysis. Normalize angular differences
before comparing them:

```text
delta = atan2(sin(b - a), cos(b - a))
```

This avoids errors at the `-π`/`π` wrap. Cluster angles with a tolerance; framework snapshots and
actor facing are not perfectly exact.

Possible geometric findings include:

- cardinal/intercardinal destinations;
- clockwise or counter-clockwise order;
- approximately 90-degree separation;
- actor movement from a shared center to assigned positions;
- cones suggested by source rotation;
- ground-targeted locations suggested by `targetPosition`.

Geometry is not the same as an authoritative AoE shape. A radius, cone angle, donut inner radius,
cross width, or snapshot time cannot be recovered from positions alone. Such values require repeated
death/survival boundaries, game data, a trusted encounter implementation, or controlled in-game
testing. Label inferred dimensions as estimates and include safety margins separately.

The game’s own telegraph is not represented as a reusable “projection” object in this contract.
Any overlay built from the evidence is a reconstruction.

## Cross-pull comparison and confidence

Separate observation from inference:

- **Observed:** exact records, IDs, actor snapshots, ordering, and measured intervals.
- **Inferred:** mechanic role, safe/dangerous meaning, shape, assignment, or a rule predicted from
  an allocation pattern.
- **Confirmed:** inference repeated across independent pulls or verified against another reliable
  source or controlled in-game behavior.

For random mechanics, record the number of pulls and occurrences. “16 of 16 actors across two
pulls followed this order” is much stronger and more reproducible than “IDs seem ordered.” It still
does not prove the game will preserve the rule after a patch.

A robust overlay or trigger should fail closed when it depends on a provisional prediction:

1. Detect the complete expected actor set.
2. Freeze the predicted mapping for that occurrence.
3. Validate the earliest observable real behavior against the prediction.
4. Hide or disable the output immediately on mismatch.
5. Clear state on wipe, combat end, duty exit, timeout, or actor-set invalidation.

Never silently substitute action names when an expected ID is absent.

## Health and completeness checks

Inspect every `health` record plus `session_end` counters before claiming absence:

- `rawEventsDropped` means observations were lost before normalization.
- `normalizedEventsDropped` means events were lost before serialization.
- `hookErrors` indicates action-effect observation failures.
- `warning` and writer failure state can explain a truncated session.

If any relevant counter is nonzero, positive observations remain useful, but a missing event is not
strong negative evidence. State the loss near the conclusion it affects.

Also check:

- whether the hook was available in `session_start`;
- whether the file is complete, partial, or recovered;
- whether logging started mid-duty or mid-combat;
- whether the caster was plausibly visible;
- whether the relevant mechanic completed before combat ended.

## Practical PowerShell recipes

Use streaming reads for large files. `Get-Content | ConvertFrom-Json` works but creates substantial
pipeline and memory overhead on captures with tens of thousands of lines.

### Lifecycle overview

```powershell
$capturePath = 'C:\path\encounterscope_...jsonl'

[IO.File]::ReadLines($capturePath) |
    ForEach-Object { $_ | ConvertFrom-Json } |
    Where-Object {
        $_.recordType -in @(
            'session_start', 'duty_marker', 'territory_changed',
            'combat_started', 'combat_ended', 'health', 'session_end'
        )
    } |
    Select-Object sequence, recordType, combatId,
        sessionElapsedSeconds, combatElapsedSeconds, territoryId,
        contentFinderConditionId, payload
```

### Ordered NPC casts for one pull

```powershell
$combatId = 14

[IO.File]::ReadLines($capturePath) |
    ForEach-Object { $_ | ConvertFrom-Json } |
    Where-Object {
        $_.combatId -eq $combatId -and
        $_.recordType -eq 'cast_started' -and
        $null -ne $_.payload.source.dataId
    } |
    Sort-Object combatElapsedSeconds, sequence |
    Select-Object combatElapsedSeconds,
        @{n='source'; e={$_.payload.source.npcName}},
        @{n='sourceDataId'; e={$_.payload.source.dataId}},
        @{n='actionType'; e={$_.payload.action.typeId}},
        @{n='actionId'; e={$_.payload.action.id}},
        @{n='action'; e={$_.payload.action.name}},
        @{n='castSeconds'; e={$_.payload.baseCastSeconds}},
        @{n='midCast'; e={$_.payload.observedMidCast}}
```

Do not filter players merely with `dataId -gt 0` in every context: pets and player-created actors
also have nonzero data IDs. For boss analysis, explicitly select expected NPC data IDs or exclude
known player/pet object kinds and class/job context after inspecting the data.

### Unique action identities without merging duplicate names

```powershell
[IO.File]::ReadLines($capturePath) |
    ForEach-Object { $_ | ConvertFrom-Json } |
    Where-Object { $_.recordType -in @('cast_started', 'action_resolved') } |
    Group-Object {
        '{0}|{1}|{2}' -f
            $_.recordType,
            $_.payload.action.typeId,
            $_.payload.action.id
    } |
    ForEach-Object {
        [pscustomobject]@{
            EventPoint = $_.Group[0].recordType
            TypeId = $_.Group[0].payload.action.typeId
            ActionId = $_.Group[0].payload.action.id
            Label = $_.Group[0].payload.action.name
            Count = $_.Count
        }
    }
```

### Resolution-only candidates

Build the cast identity set for the selected combat, then list resolutions absent from it. Treat the
result as candidates for inspection, not automatically as instant mechanics, because helper actors
often resolve under IDs different from the boss cast.

```powershell
$records = [IO.File]::ReadLines($capturePath) |
    ForEach-Object { $_ | ConvertFrom-Json } |
    Where-Object { $_.combatId -eq $combatId }

$castIds = @{}
$records |
    Where-Object recordType -eq 'cast_started' |
    ForEach-Object {
        $castIds[('{0}|{1}' -f $_.payload.action.typeId, $_.payload.action.id)] = $true
    }

$records |
    Where-Object recordType -eq 'action_resolved' |
    Where-Object {
        -not $castIds.ContainsKey(
            ('{0}|{1}' -f $_.payload.action.typeId, $_.payload.action.id)
        )
    } |
    Group-Object { '{0}|{1}' -f $_.payload.action.typeId, $_.payload.action.id }
```

For very large captures, avoid retaining all records as above: make two streaming passes instead.

### Compare identities between captures

Compare canonical keys, then return to the timeline for context:

```powershell
function Get-ActionKeys([string] $path, [int] $combatId) {
    $keys = @{}
    [IO.File]::ReadLines($path) |
        ForEach-Object { $_ | ConvertFrom-Json } |
        Where-Object {
            $_.combatId -eq $combatId -and
            $_.recordType -in @('cast_started', 'action_resolved')
        } |
        ForEach-Object {
            $key = '{0}|{1}|{2}' -f
                $_.recordType,
                $_.payload.action.typeId,
                $_.payload.action.id
            $keys[$key] = $true
        }
    $keys
}
```

Select the corresponding combat independently in each capture. Combat IDs are session-local and do
not identify the same boss across files.

## Validated encounter-analysis lessons

These examples illustrate why the general rules above matter.

### Duplicate labels remain distinct mechanics

Boss animation variants can share or closely resemble labels while using separate IDs. Four
observed sequence telegraphs used distinct action IDs `47671`, `47672`, `47673`, and `47674`.
Keeping the numeric identities separate allowed each sequence to trigger independently; grouping by
display name would have destroyed the distinction.

### A provisional actor-allocation prediction can be validated early

During Fertile Ground (`47514`), eight Severing Head actors (`dataId 19432`) were observed together
at arena center. In two independent captures, descending entity ID matched both:

- movement action `47489`; and
- beam resolutions `47516`/`47574`.

That was 16 of 16 actors across two pulls. The useful implementation pattern is to assign labels
from the complete centered set, bind them to the per-pull actor IDs as the actors move, and compare
each first movement against the predicted next actor. Any mismatch must remove all labels. The
literal entity IDs are never reusable, and the allocation rule remains patch-sensitive despite the
perfect sample.

### A high-level cast ID may not encode randomized sub-order

For Index, Sealed Implements can be distinguished directly:

| Boss cast | Helper action | Meaning |
|---:|---:|---|
| `48384` | `48422` Romeo's Ballad | Harp / move out |
| `48386` | `48423` Aim | Bow / move in |

By contrast, two Quadrilogy of Implements occurrences both used main cast `48909` but produced
different middle ordering among the helper actions:

- `48915` Wind Slash, `48913` Aim, `48914` Iainuki, `48912` Romeo's Ballad;
- `48915` Wind Slash, `48914` Iainuki, `48913` Aim, `48912` Romeo's Ballad.

Therefore the main Quadrilogy ID is a phase telegraph, not a complete ordering key. The subordinate
actions or visible implement state must determine order. This is a general warning against assuming
that a numerically different high-level cast always encodes every randomized mechanic parameter.

### A clear can end before the documented timeline

One clear ended during the final Quadrilogy sequence; another ended shortly after the final
All-knowing Flames began. Neither capture positively observed the later documented enrage. A combat
ending during a cast confirms that the cast began, not that it resolved or that subsequent timeline
steps do not exist.

## Reporting checklist

Every analysis should make it easy to distinguish facts from conclusions:

1. Identify the capture session, territory, duty, combat interval, and completeness.
2. Report health/drop status.
3. List canonical action identities with names as labels.
4. Separate cast starts from resolution-only/helper actions.
5. Include source data IDs and distinguish simultaneous actor instances.
6. Give monotonic times and state whether the pull began mid-combat.
7. Explain occurrence grouping and deduplication.
8. State sample counts across pulls.
9. Mark geometry, mechanic meaning, and allocation rules as observed, inferred, or confirmed.
10. Record contradictory samples instead of averaging them away.
11. State what the capture cannot establish.
12. For reusable behavior, specify reset, timeout, and fail-closed conditions.
