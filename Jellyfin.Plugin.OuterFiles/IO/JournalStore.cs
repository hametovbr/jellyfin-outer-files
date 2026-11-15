using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.OuterFiles.Domain.Plans;

namespace Jellyfin.Plugin.OuterFiles.IO;

/// <summary>
/// Append-only journals of applied plans, one JSON-lines file per operation in
/// <c>&lt;plugin data&gt;/journals</c>. A step is written as <c>start</c> before it runs and as
/// <c>done</c>/<c>skipped</c>/<c>failed</c> after, so an interrupted run is visible and undo knows exactly
/// what happened (docs/ARCHITECTURE.md, P4).
/// </summary>
public sealed class JournalStore
{
    private readonly string _folder;

    /// <summary>
    /// Initializes a new instance of the <see cref="JournalStore"/> class.
    /// </summary>
    /// <param name="folder">Folder that holds the journal files.</param>
    public JournalStore(string folder)
    {
        _folder = folder;
    }

    /// <summary>
    /// Starts a new journal for a plan.
    /// </summary>
    /// <param name="plan">The plan about to be applied.</param>
    /// <param name="kind">"links", "organize" or "undo".</param>
    /// <param name="undoOf">Id of the journal this run undoes, if any.</param>
    /// <returns>A writer; dispose it when the run ends.</returns>
    public JournalWriter Begin(Plan plan, string kind, string? undoOf = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Directory.CreateDirectory(_folder);
        var id = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];
        var writer = new JournalWriter(Path.Combine(_folder, id + ".jsonl"), id);
        writer.Write(new JournalLine
        {
            Type = "header",
            Id = id,
            Kind = kind,
            Title = plan.Title,
            Hash = plan.Hash,
            UndoOf = undoOf,
            Utc = DateTime.UtcNow,
        });
        return writer;
    }

    /// <summary>
    /// Lists journals, newest first.
    /// </summary>
    /// <param name="limit">Maximum number of journals.</param>
    /// <returns>Summaries.</returns>
    public IReadOnlyList<JournalSummary> List(int limit = 100)
    {
        if (!Directory.Exists(_folder))
        {
            return [];
        }

        return Directory.EnumerateFiles(_folder, "*.jsonl")
            .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
            .Take(Math.Max(1, limit))
            .Select(f => Read(Path.GetFileNameWithoutExtension(f)))
            .Where(j => j is not null)
            .Select(j => j!.Summary)
            .ToList();
    }

    /// <summary>
    /// Reads one journal.
    /// </summary>
    /// <param name="id">Journal id.</param>
    /// <returns>The journal, or null when it does not exist or is unreadable.</returns>
    public Journal? Read(string id)
    {
        if (string.IsNullOrEmpty(id) || id.IndexOfAny(['/', '\\', '.']) >= 0)
        {
            return null;
        }

        var path = Path.Combine(_folder, id + ".jsonl");
        if (!File.Exists(path))
        {
            return null;
        }

        var lines = new List<JournalLine>();
        foreach (var raw in File.ReadLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            try
            {
                var line = JsonSerializer.Deserialize<JournalLine>(raw, JournalLine.Options);
                if (line is not null)
                {
                    lines.Add(line);
                }
            }
            catch (JsonException)
            {
                // A torn last line after a crash: everything before it is still valid.
                break;
            }
        }

        var header = lines.FirstOrDefault(l => l.Type == "header");
        return header is null ? null : new Journal(header, lines);
    }

    /// <summary>
    /// Appends a line to an existing journal (used to mark a journal as undone).
    /// </summary>
    /// <param name="id">Journal id.</param>
    /// <param name="line">Line to append.</param>
    public void Append(string id, JournalLine line)
    {
        var path = Path.Combine(_folder, id + ".jsonl");
        File.AppendAllText(path, JsonSerializer.Serialize(line, JournalLine.Options) + "\n", Encoding.UTF8);
    }
}

/// <summary>
/// Writes lines to one journal and flushes each line to disk before returning.
/// </summary>
public sealed class JournalWriter : IDisposable
{
    private readonly FileStream _stream;
    private int _seq;

    internal JournalWriter(string path, string id)
    {
        Id = id;
        _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
    }

    /// <summary>
    /// Gets the journal id.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Records that a step is about to run.
    /// </summary>
    /// <param name="op">The operation.</param>
    /// <returns>The step sequence number.</returns>
    public int Start(Operation op)
    {
        var seq = ++_seq;
        Write(new JournalLine { Type = "step", Seq = seq, State = "start", Op = op, Utc = DateTime.UtcNow });
        return seq;
    }

    /// <summary>
    /// Records the outcome of a step.
    /// </summary>
    /// <param name="seq">Sequence number from <see cref="Start"/>.</param>
    /// <param name="op">The operation.</param>
    /// <param name="state">done, skipped or failed.</param>
    /// <param name="error">Error message for failed steps.</param>
    public void Finish(int seq, Operation op, string state, string? error = null)
        => Write(new JournalLine { Type = "step", Seq = seq, State = state, Op = op, Error = error, Utc = DateTime.UtcNow });

    /// <summary>
    /// Records the end of the run.
    /// </summary>
    /// <param name="state">completed or failed.</param>
    public void End(string state) => Write(new JournalLine { Type = "end", State = state, Utc = DateTime.UtcNow });

    /// <inheritdoc />
    public void Dispose() => _stream.Dispose();

    internal void Write(JournalLine line)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(line, JournalLine.Options) + "\n");
        _stream.Write(bytes);
        _stream.Flush(flushToDisk: true);
    }
}

/// <summary>
/// One line of a journal file.
/// </summary>
public sealed class JournalLine
{
    internal static readonly JsonSerializerOptions Options = new(PlanJson.Options)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Gets or sets the line type: header, step, end, undone.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets the journal id (header).</summary>
    public string? Id { get; set; }

    /// <summary>Gets or sets the kind of run (header).</summary>
    public string? Kind { get; set; }

    /// <summary>Gets or sets the plan title (header).</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the plan hash (header).</summary>
    public string? Hash { get; set; }

    /// <summary>Gets or sets the id of the journal this run undoes (header), or of the run that undid this one (undone).</summary>
    public string? UndoOf { get; set; }

    /// <summary>Gets or sets the step number.</summary>
    public int? Seq { get; set; }

    /// <summary>Gets or sets the step or run state.</summary>
    public string? State { get; set; }

    /// <summary>Gets or sets the operation of a step.</summary>
    public Operation? Op { get; set; }

    /// <summary>Gets or sets the error of a failed step.</summary>
    public string? Error { get; set; }

    /// <summary>Gets or sets the time of the line.</summary>
    public DateTime Utc { get; set; }
}

/// <summary>
/// A journal read back from disk.
/// </summary>
/// <param name="Header">The header line.</param>
/// <param name="Lines">All lines including the header.</param>
public sealed record Journal(JournalLine Header, IReadOnlyList<JournalLine> Lines)
{
    /// <summary>
    /// Gets the operations that completed, in the order they ran. Only these are undone.
    /// </summary>
    public IReadOnlyList<Operation> Applied => Lines
        .Where(l => l.Type == "step" && l.State == "done" && l.Op is not null)
        .Select(l => l.Op!)
        .ToList();

    /// <summary>
    /// Gets the end state: completed, failed, or interrupted when the run never wrote an end line.
    /// </summary>
    public string State => Lines.LastOrDefault(l => l.Type == "end")?.State ?? "interrupted";

    /// <summary>
    /// Gets the id of the run that undid this journal, if any.
    /// </summary>
    public string? UndoneBy => Lines.LastOrDefault(l => l.Type == "undone")?.UndoOf;

    /// <summary>
    /// Gets a short summary.
    /// </summary>
    public JournalSummary Summary => new(
        Header.Id ?? string.Empty,
        Header.Kind ?? string.Empty,
        Header.Title ?? string.Empty,
        Header.Utc,
        State,
        Applied.Count,
        Lines.Count(l => l.Type == "step" && l.State == "failed"),
        Header.UndoOf,
        UndoneBy);
}

/// <summary>
/// Summary of a journal for lists.
/// </summary>
/// <param name="Id">Journal id.</param>
/// <param name="Kind">links, organize or undo.</param>
/// <param name="Title">Plan title.</param>
/// <param name="StartedUtc">Start time.</param>
/// <param name="State">completed, failed or interrupted.</param>
/// <param name="Applied">Steps that completed.</param>
/// <param name="Failed">Steps that failed.</param>
/// <param name="UndoOf">Journal this run undid.</param>
/// <param name="UndoneBy">Journal that undid this run.</param>
public sealed record JournalSummary(string Id, string Kind, string Title, DateTime StartedUtc, string State, int Applied, int Failed, string? UndoOf, string? UndoneBy);
