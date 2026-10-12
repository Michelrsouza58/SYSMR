using SYSMR.Application.Abstractions;

namespace SYSMR.Infrastructure.Persistence;

public sealed class FirebaseSyncWorker(
    SqliteSyncOutboxRepository outbox,
    SqliteStoreAccountRepository accounts,
    FirebaseStoreAccountClient firebase,
    IAuditLogRepository auditLog)
{
    public async Task RunAsync(StoreAccountIdentity account, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var pending = await outbox.GetPendingAsync(cancellationToken: cancellationToken);
                if (pending.Count > 0)
                {
                    var refreshToken = await accounts.GetRefreshTokenAsync(cancellationToken);
                    if (string.IsNullOrWhiteSpace(refreshToken))
                    {
                        await outbox.MarkFailedAsync(pending[0].Id, "Sessão Firebase indisponível. Faça login online para reativar a sincronização.", cancellationToken);
                    }
                    else
                    {
                        foreach (var syncEvent in pending)
                        {
                            if (!syncEvent.Branch.Equals(account.StoreType, StringComparison.Ordinal))
                            {
                                await outbox.MarkFailedAsync(
                                    syncEvent.Id,
                                    $"Evento da vertente '{syncEvent.Branch}' não corresponde à conta '{account.StoreType}'.",
                                    cancellationToken);
                                break;
                            }

                            try
                            {
                                refreshToken = await firebase.UploadOutboxEventAsync(
                                    account.FirebaseUid,
                                    syncEvent.Branch,
                                    syncEvent.Id,
                                    syncEvent.Payload,
                                    refreshToken,
                                    cancellationToken);
                                await outbox.UpdateRefreshTokenAsync(refreshToken, cancellationToken);
                                await outbox.MarkSyncedAsync(syncEvent, cancellationToken);
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                            {
                                throw;
                            }
                            catch (Exception exception)
                            {
                                await outbox.MarkFailedAsync(syncEvent.Id, exception.Message, cancellationToken);
                                await auditLog.RecordAsync(
                                    account.CustomerName,
                                    "Falha na sincronização Firebase",
                                    $"Evento {syncEvent.Id} ({syncEvent.Entity}/{syncEvent.EntityId}): {exception.Message}",
                                    cancellationToken);
                                break;
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                await auditLog.RecordAsync(
                    account.CustomerName,
                    "Falha no processo de sincronização",
                    exception.Message,
                    cancellationToken);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
