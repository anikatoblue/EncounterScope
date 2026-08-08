namespace EncounterScope.Core;

public enum DutyCaptureTransitionKind
{
    Start,
    Stop,
}
public sealed record DutyCaptureTransition(DutyCaptureTransitionKind Kind, string Reason, bool ObservedMidDuty);

public sealed class DutyCaptureGate
{
    public DutyCaptureGate(bool enabled)
    {
        Enabled = enabled;
    }

    public bool Enabled { get; private set; }
    public bool DutyBound { get; private set; }
    public bool Active { get; private set; }

    public DutyCaptureTransition? SetEnabled(bool enabled)
    {
        if (Enabled == enabled)
            return null;

        Enabled = enabled;
        if (!enabled && Active)
        {
            Active = false;
            return new(DutyCaptureTransitionKind.Stop, "disabled", false);
        }

        if (enabled && DutyBound && !Active)
        {
            Active = true;
            return new(DutyCaptureTransitionKind.Start, "enabled_mid_duty", true);
        }

        return null;
    }

    public DutyCaptureTransition? SetDutyBound(bool dutyBound, bool observedExistingState = false)
    {
        if (DutyBound == dutyBound)
            return null;

        DutyBound = dutyBound;
        if (dutyBound && Enabled && !Active)
        {
            Active = true;
            return new(
                DutyCaptureTransitionKind.Start,
                observedExistingState ? "observed_mid_duty" : "duty_enter",
                observedExistingState);
        }

        if (!dutyBound && Active)
        {
            Active = false;
            return new(DutyCaptureTransitionKind.Stop, "duty_exit", false);
        }

        return null;
    }

    public DutyCaptureTransition? StopForUnload()
    {
        if (!Active)
            return null;

        Active = false;
        return new(DutyCaptureTransitionKind.Stop, "unload", false);
    }
}
