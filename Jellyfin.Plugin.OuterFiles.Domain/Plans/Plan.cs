using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.OuterFiles.Domain.Plans;

/// <summary>
/// An ordered list of operations with a title. A plan is what the user reviews; its <see cref="Hash"/> is
/// what the user confirms, so the executor never applies anything other than what was shown.
/// </summary>
/// <param name="Title">Human readable summary, e.g. "Organize Jitsu wa Ore, Saikyou deshita".</param>
/// <param name="Operations">Operations in execution order.</param>
public sealed record Plan(string Title, IReadOnlyList<Operation> Operations)
{
    /// <summary>
    /// Gets a value indicating whether the plan has nothing to do.
    /// </summary>
    public bool IsEmpty => Operations.Count == 0;

    /// <summary>
    /// Gets a stable fingerprint of the operations (title excluded): same operations, same hash.
    /// </summary>
    public string Hash
    {
        get
        {
            var json = JsonSerializer.Serialize(Operations, PlanJson.Options);
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        }
    }

    /// <summary>
    /// Creates a plan that undoes the given applied operations, in reverse order.
    /// </summary>
    /// <param name="title">Title of the new plan.</param>
    /// <param name="applied">Operations that were applied, in the order they were applied.</param>
    /// <returns>The undo plan.</returns>
    public static Plan UndoOf(string title, IEnumerable<Operation> applied)
    {
        ArgumentNullException.ThrowIfNull(applied);
        return new Plan(title, applied.Reverse().Select(o => o.Inverse()).ToList());
    }
}

/// <summary>
/// The JSON settings used for hashing and for the journal, so both see the same representation.
/// </summary>
public static class PlanJson
{
    /// <summary>
    /// Gets the shared serializer options.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
