namespace SYSMR.Infrastructure.Persistence;

public sealed class StoreTypeSwitchService(
    SqliteStoreAccountRepository accounts,
    SqliteSyncOutboxRepository outbox,
    FirebaseStoreAccountClient firebase)
{
    public async Task ChangeAsync(
        StoreAccountIdentity account,
        string newStoreType,
        CancellationToken cancellationToken = default)
    {
        if (newStoreType is not ("sales" or "restaurant"))
            throw new ArgumentException("Selecione uma vertente válida para a loja.", nameof(newStoreType));
        if (newStoreType == account.StoreType)
            return;

        var queue = await outbox.GetStatusAsync(account.StoreType, cancellationToken);
        if (queue.PendingCount > 0)
            throw new InvalidOperationException(
                $"Há {queue.PendingCount} evento(s) pendente(s) na vertente atual. Aguarde a sincronização antes de alterá-la.");

        var refreshToken = await accounts.GetRefreshTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new InvalidOperationException(
                "A conta não tem uma sessão Firebase renovável. Saia da conta e faça login online antes de trocar a vertente.");

        var rotatedRefreshToken = await firebase.ChangeStoreTypeAsync(
            account.FirebaseUid,
            refreshToken,
            newStoreType,
            cancellationToken);
        await accounts.UpdateStoreTypeAndRefreshTokenAsync(newStoreType, rotatedRefreshToken, cancellationToken);
    }
}
