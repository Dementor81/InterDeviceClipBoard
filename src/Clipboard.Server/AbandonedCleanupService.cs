namespace Clipboard.Server;

public sealed class AbandonedCleanupService(
    Database database,
    EncryptedBlobStore blobs,
    AppOptions options,
    ILogger<AbandonedCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            Sweep();
            try
            {
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void Sweep()
    {
        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-options.AbandonedAfterDays);
            foreach (var id in database.DeleteAbandoned(cutoff))
                blobs.Delete(id);

            blobs.DeleteStale(database.AllFileIds(), TimeSpan.FromDays(1));
        }
        catch (Exception ex)
        {
            logger.LogError("Abandoned clipboard cleanup failed ({Type}).", ex.GetType().Name);
        }
    }
}
