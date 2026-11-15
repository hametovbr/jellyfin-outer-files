namespace Jellyfin.Plugin.OuterFiles.Domain.Plans;

/// <summary>
/// A reason why a plan must not be applied.
/// </summary>
/// <param name="Code">Stable machine readable code, asserted by tests and shown in the UI.</param>
/// <param name="Path">The path involved.</param>
/// <param name="Message">Human readable explanation.</param>
public sealed record Conflict(string Code, string Path, string Message);

/// <summary>
/// Static checks that need no file system: they run before a plan is shown and again before it is applied.
/// File system preconditions (source exists, target free) are checked by the executor at apply time.
/// </summary>
public static class PlanValidator
{
    /// <summary>A path is not absolute.</summary>
    public const string NotAbsolute = "not-absolute";

    /// <summary>A path lies outside every allowed root.</summary>
    public const string OutsideRoots = "outside-roots";

    /// <summary>Two operations write the same path.</summary>
    public const string DuplicateTarget = "duplicate-target";

    /// <summary>A move whose source equals its target.</summary>
    public const string NoOpMove = "no-op-move";

    /// <summary>A path contains a parent-directory segment.</summary>
    public const string Traversal = "path-traversal";

    /// <summary>
    /// Validates a plan.
    /// </summary>
    /// <param name="plan">The plan.</param>
    /// <param name="writableRoots">Absolute roots every written path must be inside (library roots, internal metadata root).</param>
    /// <returns>Conflicts; empty when the plan is acceptable.</returns>
    public static IReadOnlyList<Conflict> Validate(Plan plan, IReadOnlyCollection<string> writableRoots)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(writableRoots);

        var conflicts = new List<Conflict>();
        var written = new HashSet<string>(StringComparer.Ordinal);
        var roots = writableRoots.Select(TrimEnd).ToList();

        foreach (var op in plan.Operations)
        {
            foreach (var path in op.Reads.Concat(op.Writes))
            {
                if (!Path.IsPathFullyQualified(path))
                {
                    conflicts.Add(new Conflict(NotAbsolute, path, "Path must be absolute."));
                }
                else if (path.Split('/', '\\').Any(s => s == ".."))
                {
                    conflicts.Add(new Conflict(Traversal, path, "Path must not contain '..'."));
                }
            }

            foreach (var path in Touched(op))
            {
                if (Path.IsPathFullyQualified(path) && !roots.Any(r => IsInside(path, r)))
                {
                    conflicts.Add(new Conflict(OutsideRoots, path, "Path is outside the managed folders."));
                }
            }

            foreach (var path in op.Writes)
            {
                if (!written.Add(path))
                {
                    conflicts.Add(new Conflict(DuplicateTarget, path, "Two operations produce the same path."));
                }
            }

            if (op is MoveFile move && string.Equals(move.Source, move.Target, StringComparison.Ordinal))
            {
                conflicts.Add(new Conflict(NoOpMove, move.Source, "Source and target are the same."));
            }
        }

        return conflicts;
    }

    /// <summary>
    /// Tells whether <paramref name="path"/> is <paramref name="root"/> or below it.
    /// </summary>
    /// <param name="path">Absolute path.</param>
    /// <param name="root">Absolute root.</param>
    /// <returns>True when inside.</returns>
    public static bool IsInside(string path, string root)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(root);
        root = TrimEnd(root);
        return path.Equals(root, StringComparison.Ordinal)
            || path.StartsWith(root + "/", StringComparison.Ordinal)
            || path.StartsWith(root + "\\", StringComparison.Ordinal);
    }

    // Everything an operation changes on disk. A symlink's target is only read, so it may live anywhere
    // inside the library; the link itself must be inside a writable root.
    private static IEnumerable<string> Touched(Operation op) => op switch
    {
        MoveFile m => [m.Source, m.Target],
        CreateSymlink l => [l.LinkPath],
        RemoveSymlink u => [u.LinkPath],
        CreateDirectory d => [d.Path],
        RemoveEmptyDirectory r => [r.Path],
        _ => op.Writes,
    };

    private static string TrimEnd(string root) => root.Length > 1 ? root.TrimEnd('/', '\\') : root;
}
