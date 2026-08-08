using EncounterScope.Core;

namespace EncounterScope;

internal sealed record RawActionEffectObservation(
    ObservationStamp Stamp,
    uint SourceEntityId,
    byte ActionType,
    uint ActionId,
    ActionEffectHeaderReference Header,
    IReadOnlyList<ulong> TargetIds);

internal sealed record NormalizedActionEffectObservation(
    ObservedGameEvent Event,
    ResolvedCastKey? CastKey);
