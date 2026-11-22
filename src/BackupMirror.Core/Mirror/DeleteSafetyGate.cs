namespace BackupMirror.Core.Mirror;

public static class DeleteSafetyGate
{
    public const int DefaultFileThreshold = 10;

    public static bool RequiresConfirmation(MirrorPlan plan, int threshold = DefaultFileThreshold)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (threshold < 0) return false; // negative = never ask
        return plan.Deletes.Count > threshold;
    }
}
