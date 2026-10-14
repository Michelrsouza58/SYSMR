using SYSMR.Application.Abstractions;
using SYSMR.Application.Models;

namespace SYSMR.Infrastructure.Persistence;

public sealed class FirebaseSyncWorker(
    SqliteSyncOutboxRepository outbox,
    SqliteStoreAccountRepository accounts,
    FirebaseStoreAccountClient firebase,
    SqliteRestaurantRepository restaurant,
    IAuditLogRepository auditLog)
{
    public async Task RunAsync(StoreAccountIdentity account, CancellationToken cancellationToken)
    {
        var menuProjectionSynchronized = !account.StoreType.Equals("restaurant", StringComparison.Ordinal);
        string? lastMenuProjectionError = null;
        var areaProjectionSynchronized = !account.StoreType.Equals("restaurant", StringComparison.Ordinal);
        string? lastAreaProjectionError = null;
        var waiterCommandFailures = new Dictionary<string, string>(StringComparer.Ordinal);
        while (!cancellationToken.IsCancellationRequested)
        {
            var menuProjectionAttempted = false;
            var areaProjectionAttempted = false;
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

                                if (account.StoreType.Equals("restaurant", StringComparison.Ordinal) &&
                                    syncEvent.Entity.Equals("menu-item", StringComparison.Ordinal))
                                {
                                    menuProjectionAttempted = true;
                                    refreshToken = await firebase.UploadRestaurantMenuAsync(
                                        account.FirebaseUid,
                                        await restaurant.GetMenuItemsAsync(cancellationToken: cancellationToken),
                                        refreshToken,
                                        cancellationToken);
                                    await outbox.UpdateRefreshTokenAsync(refreshToken, cancellationToken);
                                    menuProjectionSynchronized = true;
                                    lastMenuProjectionError = null;
                                }

                                if (account.StoreType.Equals("restaurant", StringComparison.Ordinal) &&
                                    syncEvent.Entity is "area" or "area-tables")
                                {
                                    areaProjectionAttempted = true;
                                    refreshToken = await UploadRestaurantAreasAsync(
                                        account.FirebaseUid,
                                        refreshToken,
                                        cancellationToken);
                                    await outbox.UpdateRefreshTokenAsync(refreshToken, cancellationToken);
                                    areaProjectionSynchronized = true;
                                    lastAreaProjectionError = null;
                                }

                                if (account.StoreType.Equals("restaurant", StringComparison.Ordinal) &&
                                    syncEvent.Entity is "order" or "order-item" or "order-item-removed" or "payment")
                                {
                                    areaProjectionAttempted = true;
                                    refreshToken = await UploadRestaurantAreasAsync(
                                        account.FirebaseUid,
                                        refreshToken,
                                        cancellationToken);
                                    await outbox.UpdateRefreshTokenAsync(refreshToken, cancellationToken);
                                    areaProjectionSynchronized = true;
                                    lastAreaProjectionError = null;
                                }

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

                if (!menuProjectionSynchronized && !menuProjectionAttempted)
                {
                    try
                    {
                        var refreshToken = await accounts.GetRefreshTokenAsync(cancellationToken);
                        if (string.IsNullOrWhiteSpace(refreshToken))
                            throw new InvalidOperationException(
                                "Sessão Firebase indisponível. Faça login online para publicar o cardápio.");

                        refreshToken = await firebase.UploadRestaurantMenuAsync(
                            account.FirebaseUid,
                            await restaurant.GetMenuItemsAsync(cancellationToken: cancellationToken),
                            refreshToken,
                            cancellationToken);
                        await outbox.UpdateRefreshTokenAsync(refreshToken, cancellationToken);
                        menuProjectionSynchronized = true;
                        if (lastMenuProjectionError is not null)
                        {
                            await auditLog.RecordAsync(
                                account.CustomerName,
                                "Cardápio Firebase sincronizado",
                                "A publicação do cardápio foi retomada após uma falha.",
                                cancellationToken);
                            lastMenuProjectionError = null;
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        if (!exception.Message.Equals(lastMenuProjectionError, StringComparison.Ordinal))
                        {
                            await auditLog.RecordAsync(
                                account.CustomerName,
                                "Falha na publicação do cardápio Firebase",
                                exception.Message,
                                cancellationToken);
                            lastMenuProjectionError = exception.Message;
                        }
                    }
                }

                if (!areaProjectionSynchronized && !areaProjectionAttempted)
                {
                    try
                    {
                        var refreshToken = await accounts.GetRefreshTokenAsync(cancellationToken);
                        if (string.IsNullOrWhiteSpace(refreshToken))
                            throw new InvalidOperationException(
                                "Sessão Firebase indisponível. Faça login online para publicar áreas e mesas.");

                        refreshToken = await UploadRestaurantAreasAsync(
                            account.FirebaseUid,
                            refreshToken,
                            cancellationToken);
                        await outbox.UpdateRefreshTokenAsync(refreshToken, cancellationToken);
                        areaProjectionSynchronized = true;
                        if (lastAreaProjectionError is not null)
                        {
                            await auditLog.RecordAsync(
                                account.CustomerName,
                                "Áreas e mesas Firebase sincronizadas",
                                "A publicação das áreas e mesas foi retomada após uma falha.",
                                cancellationToken);
                            lastAreaProjectionError = null;
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        if (!exception.Message.Equals(lastAreaProjectionError, StringComparison.Ordinal))
                        {
                            await auditLog.RecordAsync(
                                account.CustomerName,
                                "Falha na publicação das áreas e mesas Firebase",
                                exception.Message,
                                cancellationToken);
                            lastAreaProjectionError = exception.Message;
                        }
                    }

                }

                if (account.StoreType.Equals("restaurant", StringComparison.Ordinal))
                    await SyncPendingWaiterCommandsAsync(account, waiterCommandFailures, cancellationToken);
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
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task<string> UploadRestaurantAreasAsync(
        string firebaseUid,
        string refreshToken,
        CancellationToken cancellationToken)
    {
        var areas = await restaurant.GetAreasAsync(cancellationToken);
        var tablesByArea = new Dictionary<Guid, IReadOnlyList<SYSMR.Application.Models.RestaurantTableRecord>>();
        var openOrdersByTable = new Dictionary<Guid, SYSMR.Application.Models.RestaurantOrderRecord?>();
        foreach (var area in areas)
        {
            var tables = await restaurant.GetTablesAsync(area.Id, cancellationToken, includeInactive: true);
            tablesByArea.Add(area.Id, tables);
            foreach (var table in tables.Where(table => table.IsActive && area.IsActive))
                openOrdersByTable.Add(table.Id, await restaurant.GetOpenOrderAsync(table.Id, cancellationToken));
        }
        return await firebase.UploadRestaurantAreasAsync(
            firebaseUid,
            areas,
            tablesByArea,
            openOrdersByTable,
            refreshToken,
            cancellationToken);
    }

    private async Task SyncPendingWaiterCommandsAsync(
        StoreAccountIdentity account,
        IDictionary<string, string> commandFailures,
        CancellationToken cancellationToken)
    {
        var refreshToken = await accounts.GetRefreshTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            const string unavailable = "Sessão Firebase indisponível; comandos dos garçons não serão processados até um login online.";
            if (!commandFailures.TryGetValue("__session", out var previousSessionError) ||
                !previousSessionError.Equals(unavailable, StringComparison.Ordinal))
            {
                await auditLog.RecordAsync(
                    account.CustomerName,
                    "Sincronização de pedidos dos garçons indisponível",
                    unavailable,
                    cancellationToken);
                commandFailures["__session"] = unavailable;
            }
            return;
        }

        (string RefreshToken, IReadOnlyList<WaiterCommandRecord> Commands) pending;
        try
        {
            pending = await firebase.GetPendingWaiterCommandsAsync(
                account.FirebaseUid,
                refreshToken,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (!commandFailures.TryGetValue("__session", out var previousError) ||
                !previousError.Equals(exception.Message, StringComparison.Ordinal))
            {
                await auditLog.RecordAsync(
                    account.CustomerName,
                    "Falha ao consultar pedidos dos garçons",
                    exception.Message,
                    cancellationToken);
                commandFailures["__session"] = exception.Message;
            }
            return;
        }

        if (commandFailures.Remove("__session"))
            await auditLog.RecordAsync(
                account.CustomerName,
                "Sincronização de pedidos dos garçons restabelecida",
                "A consulta e o processamento dos comandos pendentes foram retomados.",
                cancellationToken);

        refreshToken = pending.RefreshToken;
        await outbox.UpdateRefreshTokenAsync(refreshToken, cancellationToken);
        foreach (var command in pending.Commands)
        {
            if (string.IsNullOrWhiteSpace(command.Id))
                continue;

            try
            {
                var result = await restaurant.ProcessWaiterCommandAsync(command, cancellationToken);
                if (result.Status.Equals("applied", StringComparison.Ordinal))
                {
                    refreshToken = await UploadRestaurantAreasAsync(
                        account.FirebaseUid,
                        refreshToken,
                        cancellationToken);
                    await outbox.UpdateRefreshTokenAsync(refreshToken, cancellationToken);
                }
                refreshToken = await firebase.CompleteWaiterCommandAsync(
                    account.FirebaseUid,
                    command.Id,
                    result,
                    refreshToken,
                    cancellationToken);
                await outbox.UpdateRefreshTokenAsync(refreshToken, cancellationToken);
                commandFailures.Remove(command.Id);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                if (!commandFailures.TryGetValue(command.Id, out var previousError) ||
                    !previousError.Equals(exception.Message, StringComparison.Ordinal))
                {
                    await auditLog.RecordAsync(
                        account.CustomerName,
                        "Falha ao importar pedido do garçom",
                        $"Comando {command.Id}: {exception.Message}",
                        cancellationToken);
                    commandFailures[command.Id] = exception.Message;
                }
            }
        }
    }
}
