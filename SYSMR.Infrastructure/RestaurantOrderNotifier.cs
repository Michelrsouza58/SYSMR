namespace SYSMR.Infrastructure;

/// <summary>
/// Notifica a UI quando uma comanda muda por qualquer origem externa
/// (servidor local do garçom ou sincronização Firebase).
/// </summary>
public static class RestaurantOrderNotifier
{
    public static event EventHandler? OrderChanged;

    public static void NotifyOrderChanged()
    {
        try
        {
            DebugLog.Write($"[Notifier] Disparando OrderChanged. Assinantes: {OrderChanged?.GetInvocationList().Length ?? 0}");
            OrderChanged?.Invoke(null, EventArgs.Empty);
            DebugLog.Write("[Notifier] OrderChanged disparado.");
        }
        catch (Exception exception)
        {
            DebugLog.Write($"[Notifier] Erro ao notificar UI: {exception.Message}");
        }
    }
}
