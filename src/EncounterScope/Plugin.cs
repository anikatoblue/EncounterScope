using System.Diagnostics;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Game.DutyState;
using Dalamud.Hooking;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using EncounterScope.Core;

namespace EncounterScope;

public sealed unsafe class Plugin : IDalamudPlugin
{
    private const string Command = "/encounterscope";
    private const int MaximumActionTargets = 32;

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly IChatGui chat;
    private readonly IClientState clientState;
    private readonly IFramework framework;
    private readonly ICondition condition;
    private readonly IDutyState dutyState;
    private readonly IObjectTable objectTable;
    private readonly IPartyList partyList;
    private readonly IPluginLog log;
    private readonly MetadataResolver metadata;
    private readonly CaptureStorage storage;
    private readonly ObjectFrame objectFrame = new();
    private readonly BoundedDropQueue<RawActionEffectObservation> rawQueue = new(16_384);
    private readonly DutyCaptureGate captureGate;
    private readonly List<JsonlCaptureWriter> closingWriters = [];
    private readonly Configuration configuration;
    private readonly SettingsWindow settingsWindow;
    private Hook<ActionEffectHandler.Delegates.Receive>? actionEffectHook;
    private CaptureRuntime? active;
    private bool hookAvailable;
    private bool dutyBound;
    private bool disposed;
    private long totalHookErrors;
    private long lastHealthRawDrops;
    private long lastHealthNormalizedDrops;
    private long lastHealthHookErrors;
    private long lastHealthStatusDrops;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IChatGui chat,
        IClientState clientState,
        IFramework framework,
        ICondition condition,
        IDutyState dutyState,
        IObjectTable objectTable,
        IPartyList partyList,
        IDataManager dataManager,
        IGameInteropProvider gameInteropProvider,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.chat = chat;
        this.clientState = clientState;
        this.framework = framework;
        this.condition = condition;
        this.dutyState = dutyState;
        this.objectTable = objectTable;
        this.partyList = partyList;
        this.log = log;

        configuration = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        configuration.Normalize();
        metadata = new(dataManager);
        storage = new(
            Path.Combine(pluginInterface.ConfigDirectory.FullName, "captures"),
            new CaptureWriterOptions());

        try
        {
            actionEffectHook = gameInteropProvider.HookFromAddress<ActionEffectHandler.Delegates.Receive>(
                ActionEffectHandler.Addresses.Receive.Value,
                OnActionEffect);
            actionEffectHook.Enable();
            hookAvailable = true;
        }
        catch (Exception exception)
        {
            try
            {
                actionEffectHook?.Dispose();
            }
            catch
            {
                // The initialization exception below is the actionable failure.
            }

            actionEffectHook = null;
            log.Error(exception, "EncounterScope could not initialize the action-effect hook.");
            hookAvailable = false;
            configuration.Enabled = false;
        }

        captureGate = new(configuration.Enabled && hookAvailable);
        settingsWindow = new(
            SnapshotStatus,
            enabled => SetCaptureEnabled(enabled, report: false),
            OpenCaptureFolder);

        commandManager.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open EncounterScope or manage encounter logging and captures.",
            ShowInHelp = true,
        });
        framework.Update += OnFrameworkUpdate;
        condition.ConditionChange += OnConditionChange;
        clientState.TerritoryChanged += OnTerritoryChanged;
        dutyState.DutyStarted += OnDutyStarted;
        dutyState.DutyWiped += OnDutyWiped;
        dutyState.DutyRecommenced += OnDutyRecommenced;
        dutyState.DutyCompleted += OnDutyCompleted;
        pluginInterface.UiBuilder.Draw += settingsWindow.Draw;
        pluginInterface.UiBuilder.OpenConfigUi += settingsWindow.Open;
        pluginInterface.UiBuilder.OpenMainUi += settingsWindow.Open;

        UpdateDutyBound(IsBoundByDuty(), observedExistingState: true);
        SaveConfiguration();

        if (!hookAvailable)
        {
            Print("The action-effect hook is unavailable, so encounter logging is disabled. See the Dalamud log.");
        }
        else
        {
            log.Information("EncounterScope loaded. Capture directory: {CaptureDirectory}", storage.DirectoryPath);
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;

        framework.Update -= OnFrameworkUpdate;
        condition.ConditionChange -= OnConditionChange;
        clientState.TerritoryChanged -= OnTerritoryChanged;
        dutyState.DutyStarted -= OnDutyStarted;
        dutyState.DutyWiped -= OnDutyWiped;
        dutyState.DutyRecommenced -= OnDutyRecommenced;
        dutyState.DutyCompleted -= OnDutyCompleted;
        pluginInterface.UiBuilder.Draw -= settingsWindow.Draw;
        pluginInterface.UiBuilder.OpenConfigUi -= settingsWindow.Open;
        pluginInterface.UiBuilder.OpenMainUi -= settingsWindow.Open;
        commandManager.RemoveHandler(Command);

        actionEffectHook?.Disable();
        actionEffectHook?.Dispose();
        HandleTransition(captureGate.StopForUnload());
        foreach (var writer in closingWriters)
            writer.Dispose();
        closingWriters.Clear();
    }

    private void OnCommand(string command, string arguments)
    {
        switch (arguments.Trim().ToLowerInvariant())
        {
            case "":
            case "config":
                settingsWindow.Open();
                break;
            case "on":
                SetCaptureEnabled(true, report: true);
                break;
            case "off":
                SetCaptureEnabled(false, report: true);
                break;
            case "status":
                PrintStatus();
                break;
            case "folder":
                OpenCaptureFolder();
                break;
            default:
                Print("Usage: /encounterscope [config] | on | off | status | folder");
                break;
        }
    }

    private void SetCaptureEnabled(bool enabled, bool report)
    {
        if (enabled && !hookAvailable)
        {
            if (report)
                Print("Cannot enable encounter logging because the action-effect hook is unavailable.");
            return;
        }

        if (configuration.Enabled == enabled)
        {
            if (report)
                Print($"Encounter logging is already {(enabled ? "enabled" : "disabled")}.");
            return;
        }

        configuration.Enabled = enabled;
        SaveConfiguration();
        HandleTransition(captureGate.SetEnabled(enabled));
        if (report)
            Print($"Encounter logging {(enabled ? "enabled" : "disabled")}.");
    }

    private void SaveConfiguration()
    {
        configuration.Normalize();
        pluginInterface.SavePluginConfig(configuration);
    }

    private CaptureStatusSnapshot SnapshotStatus()
    {
        var runtime = Volatile.Read(ref active);
        return new(
            configuration.Enabled,
            hookAvailable,
            dutyBound,
            runtime?.Timeline.SessionId,
            runtime?.Writer.CurrentPath ?? storage.DirectoryPath,
            runtime?.Writer.SessionBytes ?? 0,
            rawQueue.Dropped,
            runtime?.NormalizedEventsDropped ?? 0,
            runtime?.StatusEventsDropped ?? 0,
            Interlocked.Read(ref totalHookErrors),
            runtime?.Writer.Failure?.Message);
    }

    private void PrintStatus()
    {
        var status = SnapshotStatus();
        var captureState = status.SessionId is null ? "capture idle" : $"recording {status.SessionId}";
        Print(
            $"hook={(status.HookAvailable ? "ready" : "unavailable")}; boundByDuty={status.DutyBound}; " +
            $"logging={(status.Enabled ? "on" : "off")}; {captureState}; bytes={status.SessionBytes}; " +
            $"rawDrops={status.RawDrops}; normalizedDrops={status.NormalizedDrops}; statusDrops={status.StatusDrops}; " +
            $"hookErrors={status.HookErrors}; path={status.Path}" +
            (status.WriterError is null ? string.Empty : $"; writerError={status.WriterError}"));
    }

    private void OpenCaptureFolder()
    {
        try
        {
            Directory.CreateDirectory(storage.DirectoryPath);
            Process.Start(new ProcessStartInfo(storage.DirectoryPath) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            log.Warning(exception, "EncounterScope could not open the capture directory.");
            Print($"Could not open the capture directory: {exception.Message}");
        }
    }

    private void OnFrameworkUpdate(IFramework frameworkService)
    {
        UpdateDutyBound(IsBoundByDuty());

        var runtime = Volatile.Read(ref active);
        if (runtime is not null)
        {
            runtime.Context.Update(clientState.TerritoryType, CurrentContentFinderConditionId());
            objectFrame.Refresh(objectTable, partyList);
            var actions = NormalizeRawActions(runtime, objectFrame);
            ProcessCaptureCasts(runtime, objectFrame, actions);
            PublishActions(runtime, actions);
            ProcessCaptureStatuses(runtime, objectFrame);
            PublishHealthIfChanged(runtime);

            if (runtime.Writer.Failure is { } failure)
            {
                Print($"Encounter logging stopped because the writer failed: {failure.Message}");
                configuration.Enabled = false;
                SaveConfiguration();
                _ = captureGate.SetEnabled(false);
                StopSession("writer_failure", failure.Message);
            }
        }
        else
        {
            while (rawQueue.TryDequeue(out _))
            {
            }
        }

        ReapClosingWriters();
    }

    private void OnConditionChange(ConditionFlag flag, bool value)
    {
        if (flag is ConditionFlag.BoundByDuty or ConditionFlag.BoundByDuty56 or ConditionFlag.BoundByDuty95)
        {
            UpdateDutyBound(IsBoundByDuty());
            return;
        }

        if (flag != ConditionFlag.InCombat || Volatile.Read(ref active) is not { } runtime)
            return;

        if (value)
        {
            if (runtime.Timeline.StartCombat("condition_enter", false, runtime.Context.Snapshot) is { } started)
                runtime.Publish(started);
        }
        else
        {
            objectFrame.Refresh(objectTable, partyList);
            EndTrackedCasts(runtime, "combat_ended");
            EndTrackedStatuses(runtime, "combat_ended");
            if (runtime.Timeline.EndCombat("condition_exit", runtime.Context.Snapshot) is { } ended)
                runtime.Publish(ended);
        }
    }

    private void OnTerritoryChanged(uint territoryId)
    {
        if (Volatile.Read(ref active) is not { } runtime)
            return;

        var previous = runtime.Context.Snapshot.TerritoryId;
        EndTrackedStatuses(runtime, "territory_changed");
        runtime.Context.Update(territoryId, CurrentContentFinderConditionId());
        runtime.Publish(RecordTypes.TerritoryChanged, new TerritoryChangedPayload(previous, territoryId));
    }

    private void OnDutyStarted(IDutyStateEventArgs _) =>
        Volatile.Read(ref active)?.Publish(RecordTypes.DutyMarker, new DutyMarkerPayload("started", "duty_state"));

    private void OnDutyWiped(IDutyStateEventArgs eventArgs)
    {
        if (Volatile.Read(ref active) is not { } runtime)
            return;

        objectFrame.Refresh(objectTable, partyList);
        EndTrackedCasts(runtime, "wipe");
        EndTrackedStatuses(runtime, "wipe");
        if (runtime.Timeline.EndCombat("wipe", runtime.Context.Snapshot) is { } ended)
            runtime.Publish(ended);
        runtime.Publish(RecordTypes.DutyMarker, new DutyMarkerPayload("wiped", "duty_state"));
    }

    private void OnDutyRecommenced(IDutyStateEventArgs _) =>
        Volatile.Read(ref active)?.Publish(RecordTypes.DutyMarker, new DutyMarkerPayload("recommenced", "duty_state"));

    private void OnDutyCompleted(IDutyStateEventArgs _) =>
        Volatile.Read(ref active)?.Publish(RecordTypes.DutyMarker, new DutyMarkerPayload("completed", "duty_state"));

    private void UpdateDutyBound(bool bound, bool observedExistingState = false)
    {
        dutyBound = bound;
        HandleTransition(captureGate.SetDutyBound(bound, observedExistingState));
    }

    private void HandleTransition(DutyCaptureTransition? transition)
    {
        if (transition is null)
            return;

        if (transition.Kind == DutyCaptureTransitionKind.Start)
            StartSession(transition.Reason, transition.ObservedMidDuty);
        else
            StopSession(transition.Reason, null);
    }

    private void StartSession(string reason, bool observedMidDuty)
    {
        if (!hookAvailable || Volatile.Read(ref active) is not null)
            return;

        var context = new SessionContextState(clientState.TerritoryType, CurrentContentFinderConditionId());
        var timeline = new CaptureTimeline(SystemEventClock.Instance);
        var writer = new JsonlCaptureWriter(
            storage,
            timeline.SessionId,
            DateTimeOffset.UtcNow,
            (recordType, payload) => timeline.Create(recordType, payload, context.Snapshot));
        var runtime = new CaptureRuntime(context, timeline, writer);
        Volatile.Write(ref active, runtime);

        runtime.Publish(
            RecordTypes.SessionStart,
            new SessionStartPayload(
                reason,
                typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "unknown",
                observedMidDuty,
                hookAvailable,
                "English"));

        if (dutyState.IsDutyStarted)
            runtime.Publish(RecordTypes.DutyMarker, new DutyMarkerPayload("started", "observed_existing_state"));

        if (condition[ConditionFlag.InCombat] &&
            timeline.StartCombat("observed_mid_combat", true, context.Snapshot) is { } started)
        {
            runtime.Publish(started);
        }

        lastHealthRawDrops = 0;
        lastHealthNormalizedDrops = 0;
        lastHealthHookErrors = 0;
        lastHealthStatusDrops = 0;
        Print($"Encounter logging started: {timeline.SessionId}. Files: {storage.DirectoryPath}");
    }

    private void StopSession(string reason, string? writerFailure)
    {
        var runtime = Interlocked.Exchange(ref active, null);
        if (runtime is null)
            return;

        objectFrame.Refresh(objectTable, partyList);
        var actions = NormalizeRawActions(runtime, objectFrame);
        ProcessCaptureCasts(runtime, objectFrame, actions);
        PublishActions(runtime, actions);
        ProcessCaptureStatuses(runtime, objectFrame);
        EndTrackedCasts(runtime, reason);
        EndTrackedStatuses(runtime, reason);
        if (runtime.Timeline.EndCombat(reason, runtime.Context.Snapshot) is { } ended)
            runtime.Publish(ended);

        runtime.Publish(
            RecordTypes.SessionEnd,
            new SessionEndPayload(
                reason,
                runtime.RawEventsDropped,
                runtime.NormalizedEventsDropped,
                runtime.StatusEventsDropped,
                writerFailure));
        runtime.CastTracker.Clear();
        runtime.StatusTracker.Clear();
        runtime.Writer.Complete();
        closingWriters.Add(runtime.Writer);
        Print($"Encounter logging stopped ({reason}). Session bytes queued/written: {runtime.Writer.SessionBytes}.");
    }

    private void ProcessCaptureCasts(
        CaptureRuntime runtime,
        ObjectFrame frame,
        IReadOnlyList<NormalizedActionEffectObservation> actions)
    {
        var stamp = runtime.Timeline.Observe(runtime.Context.Snapshot);
        var resolvedActions = actions
            .Where(action => action.CastKey is not null)
            .Select(action => action.CastKey!.Value)
            .ToHashSet();
        var transitions = runtime.CastTracker.Update(
            frame.Casts,
            frame.PresentBattleActorIds,
            stamp.SessionElapsedSeconds,
            resolvedActions);
        PublishCastTransitions(runtime, frame, stamp, transitions);
    }

    private CastStartedPayload CreateCastPayload(CastBegan cast, ObjectFrame frame)
    {
        var snapshot = cast.Snapshot;
        var sourceObject = frame.FindByGameObjectId(snapshot.GameObjectId);
        var targetObject = snapshot.TargetGameObjectId == 0
            ? null
            : frame.FindByGameObjectId(snapshot.TargetGameObjectId);
        return new(
            cast.CastObservationId,
            metadata.ResolveAction(snapshot.ActionType, snapshot.ActionId),
            metadata.ResolveActor(sourceObject, snapshot.GameObjectId),
            snapshot.TargetGameObjectId == 0
                ? null
                : metadata.ResolveActor(targetObject, snapshot.TargetGameObjectId),
            snapshot.CurrentCastSeconds,
            snapshot.BaseCastSeconds,
            snapshot.TotalCastSeconds,
            snapshot.Interruptible,
            cast.ObservedMidCast);
    }

    private CastTerminalPayload CreateCastPayload(CastEnded cast, ObjectFrame frame)
    {
        var snapshot = cast.Snapshot;
        var sourceObject = frame.FindByGameObjectId(snapshot.GameObjectId);
        var targetObject = snapshot.TargetGameObjectId == 0
            ? null
            : frame.FindByGameObjectId(snapshot.TargetGameObjectId);
        return new(
            cast.CastObservationId,
            metadata.ResolveAction(snapshot.ActionType, snapshot.ActionId),
            metadata.ResolveActor(sourceObject, snapshot.GameObjectId),
            snapshot.TargetGameObjectId == 0
                ? null
                : metadata.ResolveActor(targetObject, snapshot.TargetGameObjectId),
            cast.ObservedDurationSeconds,
            snapshot.CurrentCastSeconds,
            snapshot.BaseCastSeconds,
            snapshot.TotalCastSeconds,
            cast.ObservedMidCast,
            cast.Reason);
    }

    private void EndTrackedCasts(CaptureRuntime runtime, string reason)
    {
        var stamp = runtime.Timeline.Observe(runtime.Context.Snapshot);
        PublishCastTransitions(
            runtime,
            objectFrame,
            stamp,
            runtime.CastTracker.EndAll(stamp.SessionElapsedSeconds, reason));
    }

    private void PublishCastTransitions(
        CaptureRuntime runtime,
        ObjectFrame frame,
        ObservationStamp stamp,
        IReadOnlyList<CastTransition> transitions)
    {
        foreach (var transition in transitions)
        {
            if (transition is CastBegan began)
                runtime.Publish(ObservedGameEvent.From(stamp, RecordTypes.CastStarted, CreateCastPayload(began, frame)));
            else if (transition is CastEnded ended)
                runtime.Publish(ObservedGameEvent.From(stamp, ended.RecordType, CreateCastPayload(ended, frame)));
        }
    }

    private void ProcessCaptureStatuses(CaptureRuntime runtime, ObjectFrame frame)
    {
        var stamp = runtime.Timeline.Observe(runtime.Context.Snapshot);
        var snapshots = frame.Statuses
            .Select(status => status with
            {
                StackCount = metadata.ResolveStatusStackCount(status.StatusId, status.Parameter),
            })
            .ToArray();
        PublishStatusTransitions(
            runtime,
            frame,
            stamp,
            runtime.StatusTracker.Update(
                snapshots,
                frame.PresentStatusActorIds,
                stamp.SessionElapsedSeconds,
                frame.StatusSnapshotActorIds));
    }

    private void EndTrackedStatuses(CaptureRuntime runtime, string reason)
    {
        var stamp = runtime.Timeline.Observe(runtime.Context.Snapshot);
        PublishStatusTransitions(runtime, objectFrame, stamp, runtime.StatusTracker.EndAll(reason));
    }

    private void PublishStatusTransitions(
        CaptureRuntime runtime,
        ObjectFrame frame,
        ObservationStamp stamp,
        IReadOnlyList<StatusTransition> transitions)
    {
        var observedUtc = DateTimeOffset.Parse(
            stamp.TimestampUtc,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind);
        foreach (var transition in transitions)
        {
            var snapshot = transition.Snapshot;
            var sourceObject = frame.FindByEntityId(snapshot.SourceEntityId);
            var source = metadata.ResolveActor(sourceObject, snapshot.SourceEntityId, snapshot.SourceEntityId);
            var target = metadata.ResolveActor(
                frame.FindByGameObjectId(snapshot.TargetGameObjectId),
                snapshot.TargetGameObjectId);
            var expirationUtc = transition.PredictedExpirationSeconds is { } expirationSeconds
                ? TimestampFormatting.Utc(observedUtc.AddSeconds(expirationSeconds - stamp.SessionElapsedSeconds))
                : null;
            var payload = new StatusLifecyclePayload(
                transition.StatusObservationId,
                metadata.ResolveStatus(snapshot.StatusId),
                source,
                target,
                snapshot.Parameter,
                snapshot.StackCount,
                snapshot.RemainingDurationSeconds,
                expirationUtc,
                transition.ObservedMidStatus,
                transition is StatusUpdated updated ? updated.Changes : null,
                transition is StatusRemoved removed ? removed.Reason : null);
            var recordType = transition switch
            {
                StatusGained => RecordTypes.StatusGained,
                StatusUpdated => RecordTypes.StatusUpdated,
                StatusRemoved => RecordTypes.StatusRemoved,
                _ => throw new InvalidOperationException("Unknown status transition."),
            };
            runtime.PublishStatus(ObservedGameEvent.From(stamp, recordType, payload));
        }
    }

    private List<NormalizedActionEffectObservation> NormalizeRawActions(CaptureRuntime runtime, ObjectFrame frame)
    {
        var actions = new List<NormalizedActionEffectObservation>();
        while (rawQueue.TryDequeue(out var raw))
        {
            if (raw!.Stamp.SessionId != runtime.Timeline.SessionId)
                continue;

            var sourceObject = frame.FindByEntityId(raw.SourceEntityId);
            if (sourceObject is not null && !frame.IsEncounterActor(sourceObject))
                continue;

            var source = metadata.ResolveActor(
                sourceObject,
                raw.SourceEntityId,
                raw.SourceEntityId);
            var targets = raw.TargetIds
                .Select(id => metadata.ResolveActor(frame.FindByGameObjectId(id), id))
                .ToArray();
            var payload = new ActionResolvedPayload(
                metadata.ResolveAction(raw.ActionType, raw.ActionId),
                source,
                raw.Header,
                targets);
            ResolvedCastKey? castKey = sourceObject is not null
                ? new ResolvedCastKey(sourceObject.GameObjectId, raw.ActionType, raw.ActionId)
                : null;
            actions.Add(new(
                ObservedGameEvent.From(raw.Stamp, RecordTypes.ActionResolved, payload),
                castKey));
        }

        return actions;
    }

    private static void PublishActions(
        CaptureRuntime runtime,
        IReadOnlyList<NormalizedActionEffectObservation> actions)
    {
        foreach (var action in actions)
            runtime.Publish(action.Event);
    }

    private void PublishHealthIfChanged(CaptureRuntime runtime)
    {
        var rawDrops = runtime.RawEventsDropped;
        var normalizedDrops = runtime.NormalizedEventsDropped;
        var hookErrors = runtime.HookErrors;
        var statusDrops = runtime.StatusEventsDropped;
        if (rawDrops == lastHealthRawDrops &&
            normalizedDrops == lastHealthNormalizedDrops &&
            hookErrors == lastHealthHookErrors &&
            statusDrops == lastHealthStatusDrops)
        {
            return;
        }

        lastHealthRawDrops = rawDrops;
        lastHealthNormalizedDrops = normalizedDrops;
        lastHealthHookErrors = hookErrors;
        lastHealthStatusDrops = statusDrops;
        runtime.Publish(
            RecordTypes.Health,
            new HealthPayload(
                rawDrops,
                normalizedDrops,
                statusDrops,
                hookErrors,
                runtime.Writer.SessionBytes,
                "One or more capture events were dropped or rejected."));
    }

    private void ReapClosingWriters()
    {
        for (var i = closingWriters.Count - 1; i >= 0; i--)
        {
            var writer = closingWriters[i];
            if (!writer.IsCompleted)
                continue;

            if (writer.Failure is { } failure)
                log.Warning(failure, "EncounterScope capture writer closed with an error.");
            writer.Dispose();
            closingWriters.RemoveAt(i);
        }
    }

    private bool IsBoundByDuty() =>
        condition[ConditionFlag.BoundByDuty] ||
        condition[ConditionFlag.BoundByDuty56] ||
        condition[ConditionFlag.BoundByDuty95];

    private uint? CurrentContentFinderConditionId()
    {
        var rowId = dutyState.ContentFinderCondition.RowId;
        return rowId == 0 ? null : rowId;
    }

    private void Print(string message) => chat.Print($"[EncounterScope] {message}");

    private void OnActionEffect(
        uint casterEntityId,
        Character* caster,
        Vector3* targetPosition,
        ActionEffectHandler.Header* header,
        ActionEffectHandler.TargetEffects* effects,
        GameObjectId* targetIds)
    {
        var runtime = Volatile.Read(ref active);
        try
        {
            if (runtime is null || header is null)
                return;

            var count = Math.Min((int)header->NumTargets, MaximumActionTargets);
            var targets = new ulong[count];
            for (var i = 0; i < count; i++)
                targets[i] = targetIds is null ? 0 : targetIds[i];

            var position = targetPosition is null
                ? null
                : new Vector3Value(targetPosition->X, targetPosition->Y, targetPosition->Z);
            var rotationRadians = header->RotationInt / 65535f * MathF.Tau - MathF.PI;
            var headerReference = new ActionEffectHeaderReference(
                IdFormatting.GameObjectId(header->AnimationTargetId),
                header->GlobalSequence,
                header->AnimationLock,
                IdFormatting.EntityId(header->BallistaEntityId),
                header->SourceSequence,
                header->RotationInt,
                rotationRadians,
                header->SpellId,
                header->AnimationVariation,
                header->Flags,
                header->ShowInLog,
                header->ForceAnimationLock,
                header->NumTargets,
                position);
            var raw = new RawActionEffectObservation(
                runtime.Timeline.Observe(runtime.Context.Snapshot),
                casterEntityId,
                (byte)header->ActionType,
                header->ActionId,
                headerReference,
                targets);
            if (!rawQueue.TryEnqueue(raw))
                runtime.IncrementRawDrop();
        }
        catch
        {
            Interlocked.Increment(ref totalHookErrors);
            runtime?.IncrementHookError();
        }
        finally
        {
            actionEffectHook!.Original(casterEntityId, caster, targetPosition, header, effects, targetIds);
        }
    }
}
