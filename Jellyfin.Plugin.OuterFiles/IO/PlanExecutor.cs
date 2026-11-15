using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.OuterFiles.Domain.Plans;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OuterFiles.IO;

/// <summary>
/// The only component that changes the file system. Applies a validated plan step by step, journaling each
/// step before and after it runs, and stops at the first failure. Nothing is ever overwritten or deleted
/// except empty directories and the plugin's own symlinks.
/// </summary>
public sealed class PlanExecutor
{
    private readonly JournalStore _journals;
    private readonly ILogger<PlanExecutor> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlanExecutor"/> class.
    /// </summary>
    /// <param name="journals">Journal store.</param>
    /// <param name="logger">Logger.</param>
    public PlanExecutor(JournalStore journals, ILogger<PlanExecutor> logger)
    {
        _journals = journals;
        _logger = logger;
    }

    /// <summary>
    /// Applies a plan.
    /// </summary>
    /// <param name="plan">The plan.</param>
    /// <param name="kind">Kind recorded in the journal.</param>
    /// <param name="writableRoots">Roots every written path must be inside.</param>
    /// <param name="cancellationToken">Cancellation token; checked between steps.</param>
    /// <param name="undoOf">Journal this run undoes.</param>
    /// <returns>The result.</returns>
    public ExecutionResult Apply(Plan plan, string kind, IReadOnlyCollection<string> writableRoots, CancellationToken cancellationToken, string? undoOf = null)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var conflicts = PlanValidator.Validate(plan, writableRoots);
        if (conflicts.Count > 0)
        {
            return new ExecutionResult(null, 0, 0, conflicts, "Plan rejected by validation.");
        }

        if (plan.IsEmpty)
        {
            return new ExecutionResult(null, 0, 0, [], null);
        }

        using var journal = _journals.Begin(plan, kind, undoOf);
        var done = 0;
        var skipped = 0;
        foreach (var op in plan.Operations)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                journal.End("failed");
                return new ExecutionResult(journal.Id, done, skipped, [], "Cancelled.");
            }

            var seq = journal.Start(op);
            try
            {
                if (Run(op))
                {
                    journal.Finish(seq, op, "done");
                    done++;
                }
                else
                {
                    journal.Finish(seq, op, "skipped");
                    skipped++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PreconditionException)
            {
                journal.Finish(seq, op, "failed", ex.Message);
                journal.End("failed");
                _logger.LogWarning("Outer Files: step {Seq} of {Journal} failed: {Error}", seq, journal.Id, ex.Message);
                return new ExecutionResult(journal.Id, done, skipped, [], ex.Message);
            }
        }

        journal.End("completed");
        return new ExecutionResult(journal.Id, done, skipped, [], null);
    }

    /// <summary>
    /// Undoes a journal: the inverse of every completed step, in reverse order, as a new journaled run.
    /// </summary>
    /// <param name="journalId">Journal to undo.</param>
    /// <param name="writableRoots">Roots every written path must be inside.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the undo run.</returns>
    public ExecutionResult Undo(string journalId, IReadOnlyCollection<string> writableRoots, CancellationToken cancellationToken)
    {
        var journal = _journals.Read(journalId);
        if (journal is null)
        {
            return new ExecutionResult(null, 0, 0, [], "Journal not found.");
        }

        if (journal.UndoneBy is not null)
        {
            return new ExecutionResult(null, 0, 0, [], "Already undone by " + journal.UndoneBy + ".");
        }

        var plan = Plan.UndoOf("Undo: " + journal.Header.Title, journal.Applied);
        var result = Apply(plan, "undo", writableRoots, cancellationToken, journalId);
        if (result.Error is null && result.JournalId is not null)
        {
            _journals.Append(journalId, new JournalLine { Type = "undone", UndoOf = result.JournalId, Utc = DateTime.UtcNow });
        }

        return result;
    }

    // Returns true when the step changed something, false when the desired state was already there.
    private static bool Run(Operation op)
    {
        switch (op)
        {
            case CreateDirectory d:
                if (Directory.Exists(d.Path))
                {
                    return false;
                }

                RequireParent(d.Path);
                RequireFree(d.Path);
                Directory.CreateDirectory(d.Path);
                return true;

            case RemoveEmptyDirectory r:
                if (!Directory.Exists(r.Path))
                {
                    return false;
                }

                if (Directory.EnumerateFileSystemEntries(r.Path).Any())
                {
                    throw new PreconditionException($"Directory is not empty: {r.Path}");
                }

                Directory.Delete(r.Path, recursive: false);
                return true;

            case MoveFile m:
                if (!Exists(m.Source))
                {
                    throw new PreconditionException($"Source does not exist: {m.Source}");
                }

                if (Directory.Exists(m.Source))
                {
                    throw new PreconditionException($"Source is a directory: {m.Source}");
                }

                RequireParent(m.Target);
                RequireFree(m.Target);
                File.Move(m.Source, m.Target, overwrite: false);
                return true;

            case CreateSymlink l:
                if (IsLinkTo(l.LinkPath, l.TargetPath))
                {
                    return false;
                }

                RequireParent(l.LinkPath);
                RequireFree(l.LinkPath);
                File.CreateSymbolicLink(l.LinkPath, l.TargetPath);
                return true;

            case RemoveSymlink u:
                if (!Exists(u.LinkPath))
                {
                    return false;
                }

                if (!IsLinkTo(u.LinkPath, u.TargetPath))
                {
                    throw new PreconditionException($"Not a link to the expected target, left alone: {u.LinkPath}");
                }

                File.Delete(u.LinkPath);
                return true;

            default:
                throw new PreconditionException("Unknown operation " + op.GetType().Name);
        }
    }

    /// <summary>
    /// Tells whether a path exists as a file, directory or (possibly dangling) symlink.
    /// </summary>
    /// <param name="path">Path.</param>
    /// <returns>True when something is there.</returns>
    internal static bool Exists(string path)
        => File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null;

    /// <summary>
    /// Tells whether <paramref name="link"/> is a symlink pointing at <paramref name="target"/>.
    /// </summary>
    /// <param name="link">Link path.</param>
    /// <param name="target">Expected target.</param>
    /// <returns>True when it is.</returns>
    internal static bool IsLinkTo(string link, string target)
        => string.Equals(new FileInfo(link).LinkTarget, target, StringComparison.Ordinal);

    private static void RequireParent(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
        {
            throw new PreconditionException($"Parent folder does not exist: {parent}");
        }
    }

    private static void RequireFree(string path)
    {
        if (Exists(path))
        {
            throw new PreconditionException($"Target already exists, nothing is overwritten: {path}");
        }
    }
}

/// <summary>
/// Outcome of applying a plan.
/// </summary>
/// <param name="JournalId">Journal of the run; null when nothing ran.</param>
/// <param name="Done">Steps that changed something.</param>
/// <param name="Skipped">Steps whose result was already in place.</param>
/// <param name="Conflicts">Validation conflicts that stopped the run before it started.</param>
/// <param name="Error">Why the run stopped, or null on success.</param>
public sealed record ExecutionResult(string? JournalId, int Done, int Skipped, IReadOnlyList<Conflict> Conflicts, string? Error)
{
    /// <summary>Gets a value indicating whether the run completed.</summary>
    public bool Succeeded => Error is null;
}

/// <summary>
/// A file system precondition of a step does not hold.
/// </summary>
public sealed class PreconditionException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PreconditionException"/> class.
    /// </summary>
    /// <param name="message">Message.</param>
    public PreconditionException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PreconditionException"/> class.
    /// </summary>
    public PreconditionException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PreconditionException"/> class.
    /// </summary>
    /// <param name="message">Message.</param>
    /// <param name="innerException">Inner exception.</param>
    public PreconditionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
