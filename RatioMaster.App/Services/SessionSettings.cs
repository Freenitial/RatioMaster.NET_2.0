namespace RatioMaster.Services;

/// <summary>Canonical persisted labels for session stop conditions.</summary>
internal static class SessionSettings
{
    internal static string NormalizeStopCondition(string value) => value switch
    {
        "When uploaded >" => "When upload >",
        "When downloaded >" => "When download >",
        _ => value,
    };
}
