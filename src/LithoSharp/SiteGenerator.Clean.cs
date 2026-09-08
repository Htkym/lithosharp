namespace LithoSharp;

public sealed partial class SiteGenerator
{
    /// <summary>Atomically removes unchanged owned artifacts while preserving modified and unowned files.</summary>
    /// <param name="outputDirectory">The output directory to clean.</param>
    /// <param name="cancellationToken">Cancels cleaning before the atomic commit.</param>
    /// <returns>The removed paths, relative to the output directory.</returns>
    public async Task<IReadOnlyList<string>> CleanAsync(
        string outputDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var transaction = await OutputTransaction.CreateAsync(
            outputDirectory, preserveExisting: true, cancellationToken).ConfigureAwait(false);
        var committed = false;
        try
        {
            var removed = await transaction.RemoveStaleOwnedFilesAsync([], cancellationToken).ConfigureAwait(false);
            await transaction.PrepareOwnershipStateAsync([], cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync([]).ConfigureAwait(false);
            committed = true;
            return removed;
        }
        finally
        {
            try
            {
                if (!committed) await transaction.CleanupAsync().ConfigureAwait(false);
            }
            finally
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
