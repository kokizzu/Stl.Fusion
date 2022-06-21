using Microsoft.EntityFrameworkCore;
using Stl.Fusion.EntityFramework.Multitenancy;
using Stl.Locking;

namespace Stl.Fusion.EntityFramework.Npgsql.Operations;

public class NpgsqlDbOperationLogChangeNotifier<TDbContext> : DbServiceBase<TDbContext>,
    IOperationCompletionListener, IDisposable
    where TDbContext : DbContext
{
    public NpgsqlDbOperationLogChangeTrackingOptions<TDbContext> Options { get; }

    protected AgentInfo AgentInfo { get; }
    protected TDbContext? DbContext { get; set; }
    protected AsyncLock AsyncLock { get; }
    protected bool IsDisposed { get; set; }

    public NpgsqlDbOperationLogChangeNotifier(
        NpgsqlDbOperationLogChangeTrackingOptions<TDbContext> options,
        IServiceProvider services)
        : base(services)
    {
        Options = options;
        AgentInfo = services.GetRequiredService<AgentInfo>();
        AsyncLock = new AsyncLock(ReentryMode.CheckedFail);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (IsDisposed || !disposing) return;

        IsDisposed = true;
        using var suppressing = ExecutionContextExt.SuppressFlow();
        _ = Task.Run(async () => {
            using (await AsyncLock.Lock().ConfigureAwait(false)) {
                var dbContext = DbContext;
                if (dbContext != null)
                    await dbContext.DisposeAsync().ConfigureAwait(false);
            }
        });
    }

    public Task OnOperationCompleted(IOperation operation)
    {
        if (!StringComparer.Ordinal.Equals(operation.AgentId, AgentInfo.Id.Value)) // Only local commands require notification
            return Task.CompletedTask;
        var commandContext = CommandContext.Current;
        var tenantInfo = (TenantInfo?) null;
        if (commandContext != null) { // It's a command
            var operationScope = commandContext.Items.Get<DbOperationScope<TDbContext>>();
            if (operationScope == null || !operationScope.IsUsed) // But it didn't change anything related to TDbContext
                return Task.CompletedTask;
            tenantInfo = operationScope.TenantInfo;
        }
        // If it wasn't command, we pessimistically assume it changed something
        using var _ = ExecutionContextExt.SuppressFlow();
        Task.Run(() => Notify(tenantInfo));
        return Task.CompletedTask;
    }

    // Protected methods

    protected virtual async Task Notify(TenantInfo? tenantInfo)
    {
#pragma warning disable MA0074
        var qPayload = AgentInfo.Id.Value.Replace("'", "''");
#pragma warning restore MA0074
        TDbContext? dbContext = null;
        for (var retryIndex = 1; retryIndex <= Options.RetryCount; retryIndex++) {
            try {
                using (await AsyncLock.Lock().ConfigureAwait(false)) {
                    if (IsDisposed)
                        return;
                    dbContext = DbContext ??= CreateDbContext(tenantInfo);
                    await dbContext.Database
                        .ExecuteSqlRawAsync($"NOTIFY {Options.ChannelName}, '{qPayload}'")
                        .ConfigureAwait(false);
                }
                return;
            }
            catch (Exception e) {
                Log.LogError(e, "Notification failed - retrying ({RetryIndex})", retryIndex);
                DbContext = null;
                _ = dbContext?.DisposeAsync(); // Doesn't matter if it fails
                await Clocks.CoarseCpuClock.Delay(Options.RetryDelay).ConfigureAwait(false);
            }
        }
    }
}
