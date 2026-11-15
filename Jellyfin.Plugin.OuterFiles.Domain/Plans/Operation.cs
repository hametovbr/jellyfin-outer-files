using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.OuterFiles.Domain.Plans;

/// <summary>
/// One reversible change to the file system. Operations are pure data: planners create them, only the
/// executor in the plugin applies them (docs/ARCHITECTURE.md, P3/P4).
/// </summary>
/// <param name="Reason">Why the planner wants this change; shown in previews and stored in the journal.</param>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
[JsonDerivedType(typeof(CreateDirectory), "mkdir")]
[JsonDerivedType(typeof(RemoveEmptyDirectory), "rmdir")]
[JsonDerivedType(typeof(MoveFile), "move")]
[JsonDerivedType(typeof(CreateSymlink), "link")]
[JsonDerivedType(typeof(RemoveSymlink), "unlink")]
public abstract record Operation(string Reason)
{
    /// <summary>
    /// Gets every absolute path this operation creates or occupies once applied.
    /// </summary>
    [JsonIgnore]
    public abstract IReadOnlyList<string> Writes { get; }

    /// <summary>
    /// Gets every absolute path this operation needs to exist before it runs.
    /// </summary>
    [JsonIgnore]
    public abstract IReadOnlyList<string> Reads { get; }

    /// <summary>
    /// Returns the operation that undoes this one.
    /// </summary>
    /// <returns>The inverse operation.</returns>
    public abstract Operation Inverse();
}

/// <summary>Creates a directory that does not exist yet.</summary>
/// <param name="Path">Absolute directory path.</param>
/// <param name="Reason">Why.</param>
public sealed record CreateDirectory(string Path, string Reason) : Operation(Reason)
{
    /// <inheritdoc />
    public override IReadOnlyList<string> Writes => [Path];

    /// <inheritdoc />
    public override IReadOnlyList<string> Reads => [];

    /// <inheritdoc />
    public override Operation Inverse() => new RemoveEmptyDirectory(Path, "undo: " + Reason);
}

/// <summary>Removes a directory, only when it is empty.</summary>
/// <param name="Path">Absolute directory path.</param>
/// <param name="Reason">Why.</param>
public sealed record RemoveEmptyDirectory(string Path, string Reason) : Operation(Reason)
{
    /// <inheritdoc />
    public override IReadOnlyList<string> Writes => [];

    /// <inheritdoc />
    public override IReadOnlyList<string> Reads => [Path];

    /// <inheritdoc />
    public override Operation Inverse() => new CreateDirectory(Path, "undo: " + Reason);
}

/// <summary>Moves (renames) a file. Never overwrites: the target must not exist.</summary>
/// <param name="Source">Absolute source path.</param>
/// <param name="Target">Absolute target path.</param>
/// <param name="Reason">Why.</param>
public sealed record MoveFile(string Source, string Target, string Reason) : Operation(Reason)
{
    /// <inheritdoc />
    public override IReadOnlyList<string> Writes => [Target];

    /// <inheritdoc />
    public override IReadOnlyList<string> Reads => [Source];

    /// <inheritdoc />
    public override Operation Inverse() => new MoveFile(Target, Source, "undo: " + Reason);
}

/// <summary>Creates a symbolic link to a file.</summary>
/// <param name="LinkPath">Absolute path of the link.</param>
/// <param name="TargetPath">Absolute path the link points to.</param>
/// <param name="Reason">Why.</param>
public sealed record CreateSymlink(string LinkPath, string TargetPath, string Reason) : Operation(Reason)
{
    /// <inheritdoc />
    public override IReadOnlyList<string> Writes => [LinkPath];

    /// <inheritdoc />
    public override IReadOnlyList<string> Reads => [];

    /// <inheritdoc />
    public override Operation Inverse() => new RemoveSymlink(LinkPath, TargetPath, "undo: " + Reason);
}

/// <summary>Removes a symbolic link, only when it still points to the expected target.</summary>
/// <param name="LinkPath">Absolute path of the link.</param>
/// <param name="TargetPath">Target the link is expected to point to.</param>
/// <param name="Reason">Why.</param>
public sealed record RemoveSymlink(string LinkPath, string TargetPath, string Reason) : Operation(Reason)
{
    /// <inheritdoc />
    public override IReadOnlyList<string> Writes => [];

    /// <inheritdoc />
    public override IReadOnlyList<string> Reads => [LinkPath];

    /// <inheritdoc />
    public override Operation Inverse() => new CreateSymlink(LinkPath, TargetPath, "undo: " + Reason);
}
