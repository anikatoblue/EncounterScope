# EncounterScope Data-Capture Roadmap

This roadmap lists proposed additions to the capture contract and analysis tooling. Each raw event
must be stamped at observation time with the standard session, combat, territory, sequence, UTC,
and monotonic timing context. New capture domains must preserve player-name privacy, use bounded
queues, avoid blocking framework or native-hook threads, report losses, and document patch-sensitive
dependencies.

Schema changes require an explicit compatibility decision before implementation, as described in
`AGENTS.md`.

## Priority 1: assignment and actor state

### Status lifecycle

- [x] Add `status_gained`, `status_updated`, and `status_removed` records.
- [x] Record numeric status ID and an optional fixed-English label.
- [x] Record source and target actor references without player names.
- [x] Record stack count, duration, observed expiration time, and status parameter when available.
- [x] Distinguish refresh, stack change, source change, natural expiration, cleanse, and actor loss
      when that distinction is reliable.
- [x] Mark statuses first observed in progress so their true application time is not fabricated.
- [x] Deduplicate unchanged framework snapshots without hiding real refreshes or stack changes.
- [x] Test gain, refresh, stack changes, removal, mid-status discovery, actor disappearance, and
      multiple sources applying the same status ID.

### Tether lifecycle

- [ ] Add `tether_created`, `tether_updated`, and `tether_removed` records.
- [ ] Record source and target object/entity references, tether ID, and available parameters.
- [ ] Record first-observed and last-observed times.
- [ ] Distinguish multiple simultaneous tethers sharing one ID.
- [ ] Mark tethers first observed in progress.
- [ ] Test target changes, duplicate notifications, source/target despawn, and duty/combat cleanup.

### Headmarkers and icons

- [ ] Add headmarker/icon assignment and removal records.
- [ ] Record raw marker ID, target reference, and available source/context fields.
- [ ] Preserve raw IDs before applying any encounter-relative normalization.
- [ ] If normalized IDs are exposed, record both raw and normalized values plus the normalization
      basis and confidence.
- [ ] Test repeated markers, simultaneous assignments, target disappearance, and marker reuse.

### Actor lifecycle

- [ ] Add `actor_spawned`, `actor_state_changed`, and `actor_despawned` records for relevant battle
      actors and encounter objects.
- [ ] Record game object ID, entity ID, base/data ID, object kind, class/job, owner ID, name ID,
      model ID, hitbox radius, position, rotation, and fixed-English NPC label where available.
- [ ] Record targetable, visible, dead/alive, hostile/friendly, and casting state transitions.
- [ ] Record first-observed state separately from a confirmed creation event.
- [ ] Record last-observed state separately from a confirmed destruction event.
- [ ] Derive a per-pull anonymous actor ordinal for actors sharing a base/data ID.
- [ ] Never treat literal object/entity IDs as stable across pulls.
- [ ] Filter irrelevant ambient actors and expose loss counters for the actor event stream.
- [ ] Test simultaneous identical actors, ownership, transformation, visibility changes, targetable
      changes, death, disappearance, and reappearance.

### Cast lifecycle completion

- [x] Add `cast_completed`, `cast_cancelled`, and reserved `cast_interrupted` records.
- [x] Give every observed cast occurrence a stable capture-local `castObservationId`.
- [x] Carry the cast occurrence ID from start through its terminal event.
- [x] Record end time, observed elapsed time, source, target, action identity, and reliable termination
      reason.
- [x] Distinguish completion without a visible action resolution from cancellation.
- [x] Preserve `observedMidCast` semantics and avoid inventing a start time.
- [x] Test action changes during casting, repeated IDs separated by idle frames, source despawn,
      combat end, wipe, interruption, and completion without resolution.

## Priority 2: synchronized mechanic evidence

### Mechanic-time world snapshots

- [ ] Add bounded world snapshots at selected observation points, initially boss cast starts and
      action resolutions.
- [ ] Include relevant NPCs, encounter objects, and anonymized party actor references.
- [ ] Record position, rotation, hitbox radius, targetable state, visible state, dead/alive state,
      current cast identity, and current target where available.
- [ ] Make snapshot policy configurable by event point, actor kind, and frequency.
- [ ] Deduplicate identical actor state within one snapshot.
- [ ] Impose actor-count and byte-size limits and report truncation explicitly.
- [ ] Test large object tables, missing metadata, party pets, anonymous players, and snapshot loss.

### Action and actor metadata enrichment

- [ ] Record the metadata source and language for every resolved label.
- [ ] Add action cast type, effect range, X-axis modifier, target type, omen reference, animation
      reference, and other stable sheet fields useful for geometry analysis.
- [ ] Add BNpcName ID, BNpcBase ID, ModelChara ID, hitbox radius, owner ID, and name ID to actor
      metadata where available.
- [ ] Keep raw numeric identity canonical when metadata is missing or contradictory.
- [ ] Distinguish sheet-derived hints from runtime observations in the schema.
- [ ] Never claim that sheet range or omen data is an authoritative encounter AoE without runtime
      confirmation.
- [ ] Test duplicate names, unknown IDs, incompatible action types, missing rows, and metadata changes.

### Normalized action outcomes

- [ ] Add an optional normalized per-target outcome model for processed actions.
- [ ] Record damage, healing, shielding, miss, block, parry, immunity, critical/direct-hit flags,
      target death, and resource change when reliably represented.
- [ ] Record applied or removed status IDs and durations when present in effect results.
- [ ] Record knockback, draw-in, and displacement parameters when reliably represented.
- [ ] Preserve target association and effect ordering without serializing player names.
- [ ] Distinguish absent, unknown, and zero-valued outcomes.
- [ ] Keep raw effect-slot capture behind a separate explicit diagnostic option if added at all.
- [ ] Bound per-action outcome size and report truncation rather than allocating without limit.
- [ ] Test multi-target actions, mixed outcomes, empty slots, status-only actions, misses, deaths, and
      malformed or unsupported effect types.

### Target and selection changes

- [ ] Record relevant NPC target changes with old and new anonymous actor references.
- [ ] Record cast target, animation target, ground target, and resolved targets as separate concepts.
- [ ] Record whether a target was resolved from the live object table or remains ID-only.
- [ ] Test target swaps during casts, untargeted actions, ground targeting, and unloaded targets.

## Priority 3: visual and environmental evidence

### VFX events

- [ ] Add an optional VFX event stream for actor-attached and world-space effects.
- [ ] Record VFX path or numeric resource identity, source/target references, position, rotation,
      scale, parameters, and creation/removal times when available.
- [ ] Distinguish first observation from authoritative creation.
- [ ] Redact or omit any path or parameter that can contain personal data.
- [ ] Deduplicate repeated notifications without merging separate VFX instances.
- [ ] Isolate patch-sensitive readers and disable only this stream when signatures are unavailable.
- [ ] Test actor-attached, target-attached, world-space, looping, replaced, and orphaned VFX.

### Object effects and animation timelines

- [ ] Record object-effect IDs, parameters, source references, and observation times.
- [ ] Record actor timeline/animation IDs and state transitions where safely observable.
- [ ] Correlate animation observations with actor instances without interpreting their mechanic
      meaning in the raw capture layer.
- [ ] Record animation variation independently from normalized action identity.
- [ ] Test simultaneous clones, repeated timelines, actor replacement, and missing source metadata.

### Environment and director state

- [ ] Add privacy-safe environment-control and director-state records.
- [ ] Record director category, environment index, state, parameters, and raw numeric values needed
      for later interpretation.
- [ ] Record platform or encounter-object state transitions when represented by environment data.
- [ ] Separate ordinary duty lifecycle markers from mechanic-specific environment events.
- [ ] Treat all numeric environment values as duty- and patch-specific until validated.
- [ ] Test phase changes, repeated values, territory transitions, recommence, completion, and stale
      notifications.

### Throttled movement traces

- [ ] Add an optional movement stream for explicitly selected actor kinds or base/data IDs.
- [ ] Record timestamped position and rotation samples plus actor identity.
- [ ] Emit only after configurable distance, angle, or time thresholds.
- [ ] Support short high-frequency sampling windows opened by selected casts or actor spawns.
- [ ] Record sampling policy, effective frequency, dropped samples, and truncation.
- [ ] Add delta or batch encoding only if it does not obscure the raw timing semantics.
- [ ] Test stationary actors, teleportation, continuous movement, rotation in place, despawn, and
      queue saturation.

## Priority 4: correlation and analysis products

### Derived mechanic correlation

- [ ] Add a post-capture correlation layer; do not execute it in native hooks.
- [ ] Link cast starts, terminal cast events, helper actions, resolutions, statuses, VFX, tethers,
      markers, environment events, and world snapshots into candidate mechanic windows.
- [ ] Preserve references to original session ID and sequence numbers.
- [ ] Record correlation method, confidence, time window, and contradictory evidence.
- [ ] Never overwrite or remove raw observations during correlation.
- [ ] Allow multiple candidate parents when evidence is ambiguous.
- [ ] Test identical action IDs in overlapping mechanics, helper actors, delayed resolutions, pulses,
      and incomplete captures.

### Encounter interval summaries

- [ ] Produce derived summaries for each combat interval.
- [ ] Include duration, observed hostile base/data IDs, distinctive action identities, first and last
      boss-like casts, completion markers, drop counters, and mid-combat observation state.
- [ ] Do not label a combat interval as a specific boss solely from `combatId` or duration.
- [ ] Allow externally maintained encounter identification rules using territory, duty, actor, and
      action identities.
- [ ] Test hallway combat, traps, short add intervals, wipes, clears, and combat-flag gaps.

### Capture-quality summary

- [ ] Emit or generate a prominent session quality report.
- [ ] Include raw and normalized drops, per-stream drops, hook errors, unavailable modules,
      mid-duty/mid-combat startup, incomplete segments, writer failures, truncation, and recovery.
- [ ] Identify the time ranges affected by failures when possible.
- [ ] Distinguish positive evidence from the reliability of negative evidence.
- [ ] Include schema version, plugin version, enabled capture modules, and sampling policies.

### Reusable analysis export

- [ ] Export canonical action inventories keyed by event point, action type, and action ID.
- [ ] Export actor inventories keyed by combat-local actor identity and base/data ID.
- [ ] Export ordered cast timelines, resolution-only candidates, helper-action clusters, and measured
      intervals.
- [ ] Export positions and rotations without adding inferred AoE shapes to raw data.
- [ ] Preserve null labels and unknown metadata.
- [ ] Include sample counts across occurrences, pulls, and sessions.

## Cross-cutting safety and contract work

- [ ] Give each optional capture stream an independent availability and enabled state.
- [ ] Keep native detours limited to bounded raw snapshots and invoke every original function exactly
      once in `finally`.
- [ ] Perform metadata lookup, deduplication, correlation, compression, and file I/O away from native
      hooks.
- [ ] Define bounded queue and payload limits for every stream.
- [ ] Add per-stream drop and error counters to `health`, status output, and session summaries.
- [ ] Stop only the failing optional stream when safe; stop the session when data integrity or writer
      safety cannot be maintained.
- [ ] Preserve observation-time UTC, monotonic clocks, combat context, and territory/duty context on
      every new record.
- [ ] Use exact numeric IDs as canonical identity; keep names and inferred meanings informational.
- [ ] Never read or serialize player-character names.
- [ ] Exclude capture paths, raw object-instance IDs, and player-identifying data from shareable
      derived artifacts unless the artifact explicitly requires private local evidence.
- [ ] Document storage impact and retention behavior for each high-volume stream.
- [ ] Add configuration and status UI for optional streams, sampling policies, and availability.
- [ ] Add fixtures and schema validation for every new record type.
- [ ] Update `AGENTS.md`, `README.md`, the capture-analysis handbook, and schema documentation in the
      same change as any implemented roadmap item.

## Completion criteria for each capture domain

An item is complete only when:

- [ ] The normalized core contract is documented and independent of Dalamud adapters.
- [ ] Observation is bounded, exception-safe, and stamped before queueing.
- [ ] Player-name privacy is covered by automated tests.
- [ ] Unknown, missing, duplicate, mid-observation, and out-of-order cases are tested.
- [ ] Queue loss and module failure are visible in health and status reporting.
- [ ] JSONL ordering, round-trip parsing, and numeric identity are tested.
- [ ] Storage and performance impact have been measured with worst-case synthetic input.
- [ ] In-game verification covers ordinary operation, wipe/reset, unload, and unavailable-data paths.
- [ ] Documentation states what the new evidence can and cannot establish.
