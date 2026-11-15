using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.OuterFiles.Services;

/// <summary>
/// Single writer for the plugin: link reconciliation, organize apply and undo never run at the same time
/// (docs/ARCHITECTURE.md, P8). A second request does not queue up; it is told what is running.
/// </summary>
public sealed class OperationGate : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private volatile string? _current;

    /// <summary>
    /// Gets the name of the running operation, or null when idle.
    /// </summary>
    public string? Current => _current;

    /// <summary>
    /// Runs <paramref name="work"/> if nothing else is running.
    /// </summary>
    /// <typeparam name="T">Result type.</typeparam>
    /// <param name="name">Operation name shown to the user.</param>
    /// <param name="work">The work.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>(true, result) when it ran; (false, default) when another operation was running.</returns>
    public async Task<(bool Ran, T? Result)> TryRunAsync<T>(string name, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (!await _semaphore.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return (false, default);
        }

        _current = name;
        try
        {
            return (true, await work(cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            _current = null;
            _semaphore.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _semaphore.Dispose();
}
