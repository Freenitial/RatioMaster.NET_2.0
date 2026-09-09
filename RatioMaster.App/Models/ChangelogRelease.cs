namespace RatioMaster.Models;

using System.Collections.Generic;

public sealed record ChangelogItem(string Title, string Description)
{
    public bool HasTitle => Title.Length > 0;
}

public sealed record ChangelogRelease(string Version, IReadOnlyList<ChangelogItem> Changes, bool IsLatest);
