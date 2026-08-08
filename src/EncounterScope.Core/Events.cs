namespace EncounterScope.Core;

public static class RecordTypes
{
    public const string SessionStart = "session_start";
    public const string SegmentStart = "segment_start";
    public const string DutyMarker = "duty_marker";
    public const string TerritoryChanged = "territory_changed";
    public const string CombatStarted = "combat_started";
    public const string CombatEnded = "combat_ended";
    public const string CastStarted = "cast_started";
    public const string CastCompleted = "cast_completed";
    public const string CastCancelled = "cast_cancelled";
    public const string CastInterrupted = "cast_interrupted";
    public const string ActionResolved = "action_resolved";
    public const string StatusGained = "status_gained";
    public const string StatusUpdated = "status_updated";
    public const string StatusRemoved = "status_removed";
    public const string Health = "health";
    public const string SegmentEnd = "segment_end";
    public const string SessionEnd = "session_end";
}

public readonly record struct CaptureContext(uint TerritoryId, uint? ContentFinderConditionId);

public sealed record ObservationStamp(
    string SessionId,
    string TimestampUtc,
    double SessionElapsedSeconds,
    double? CombatElapsedSeconds,
    int? CombatId,
    uint TerritoryId,
    uint? ContentFinderConditionId);

public sealed record ObservedGameEvent(
    int SchemaVersion,
    string RecordType,
    string SessionId,
    ulong Sequence,
    string TimestampUtc,
    double SessionElapsedSeconds,
    double? CombatElapsedSeconds,
    int? CombatId,
    uint TerritoryId,
    uint? ContentFinderConditionId,
    object Payload)
{
    public const int CurrentSchemaVersion = 3;

    public static ObservedGameEvent From(ObservationStamp stamp, string recordType, object payload) =>
        new(
            CurrentSchemaVersion,
            recordType,
            stamp.SessionId,
            0,
            stamp.TimestampUtc,
            stamp.SessionElapsedSeconds,
            stamp.CombatElapsedSeconds,
            stamp.CombatId,
            stamp.TerritoryId,
            stamp.ContentFinderConditionId,
            payload);
}

public sealed record Vector3Value(float X, float Y, float Z);

public sealed record ActionReference(byte TypeId, string TypeName, uint Id, string? Name);

public sealed record ActorReference(
    string GameObjectId,
    string? EntityId,
    uint? DataId,
    string? ObjectKind,
    uint? ClassJobId,
    Vector3Value? Position,
    float? Rotation,
    string? NpcName)
{
    public static ActorReference Unknown(ulong gameObjectId) =>
        new(IdFormatting.GameObjectId(gameObjectId), null, null, null, null, null, null, null);

    public static ActorReference Unknown(ulong gameObjectId, uint entityId) =>
        new(
            IdFormatting.GameObjectId(gameObjectId),
            IdFormatting.EntityId(entityId),
            null,
            null,
            null,
            null,
            null,
            null);
}

public static class IdFormatting
{
    public static string GameObjectId(ulong id) => $"0x{id:X16}";
    public static string EntityId(uint id) => $"0x{id:X8}";
}

public sealed record SessionStartPayload(
    string Reason,
    string PluginVersion,
    bool ObservedMidDuty,
    bool HookAvailable,
    string LabelLanguage);

public sealed record SessionEndPayload(
    string Reason,
    long RawEventsDropped,
    long NormalizedEventsDropped,
    long StatusEventsDropped,
    string? WriterFailure);

public sealed record SegmentBoundaryPayload(int SegmentIndex, string FileName);

public sealed record DutyMarkerPayload(string Marker, string Reason);

public sealed record TerritoryChangedPayload(uint PreviousTerritoryId, uint NewTerritoryId);

public sealed record CombatBoundaryPayload(string Reason, bool ObservedMidCombat);

public sealed record CastStartedPayload(
    long CastObservationId,
    ActionReference Action,
    ActorReference Source,
    ActorReference? Target,
    float CurrentCastSeconds,
    float BaseCastSeconds,
    float TotalCastSeconds,
    bool Interruptible,
    bool ObservedMidCast);

public sealed record CastTerminalPayload(
    long CastObservationId,
    ActionReference Action,
    ActorReference Source,
    ActorReference? Target,
    double ObservedDurationSeconds,
    float CurrentCastSeconds,
    float BaseCastSeconds,
    float TotalCastSeconds,
    bool ObservedMidCast,
    string Reason);

public sealed record ActionEffectHeaderReference(
    string AnimationTargetId,
    uint GlobalSequence,
    float AnimationLockSeconds,
    string BallistaEntityId,
    ushort SourceSequence,
    ushort RotationInt,
    float RotationRadians,
    ushort SpellId,
    byte AnimationVariation,
    byte Flags,
    bool ShowInLog,
    bool ForceAnimationLock,
    byte TargetCount,
    Vector3Value? TargetPosition);

public sealed record ActionResolvedPayload(
    ActionReference Action,
    ActorReference Source,
    ActionEffectHeaderReference Header,
    IReadOnlyList<ActorReference> Targets);

public sealed record StatusReference(uint Id, string? Name);

public sealed record StatusLifecyclePayload(
    long StatusObservationId,
    StatusReference Status,
    ActorReference Source,
    ActorReference Target,
    ushort Parameter,
    byte? StackCount,
    float RemainingDurationSeconds,
    string? PredictedExpirationTimestampUtc,
    bool ObservedMidStatus,
    IReadOnlyList<string>? Changes,
    string? Reason);

public sealed record HealthPayload(
    long RawEventsDropped,
    long NormalizedEventsDropped,
    long StatusEventsDropped,
    long HookErrors,
    long SessionBytes,
    string? Warning);
