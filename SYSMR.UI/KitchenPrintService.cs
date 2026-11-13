using SYSMR.Application.Models;
using SYSMR.Infrastructure;
using SYSMR.Infrastructure.Persistence;
using SYSMR.Infrastructure.Printing;

namespace SYSMR.UI;

/// <summary>
/// Imprime na cozinha somente os itens ainda não impressos, venham do desktop ou do app do garçom.
/// Falhas ficam marcadas e só elas são reenviadas na reimpressão.
/// </summary>
public sealed class KitchenPrintService
{
    private readonly SqliteRestaurantRepository _restaurant;
    private readonly SqliteAccessControl _accessControl;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public KitchenPrintService(SqliteRestaurantRepository restaurant, SqliteAccessControl accessControl)
    {
        _restaurant = restaurant;
        _accessControl = accessControl;
    }

    public bool Enabled { get; set; }

    public async Task ProcessPendingAsync()
    {
        if (!Enabled)
        {
            DebugLog.Write("[KitchenPrint] Serviço desabilitado, ignorando.");
            return;
        }
        DebugLog.Write("[KitchenPrint] Processando pendências...");
        await PrintAsync(failedOnly: false);
    }

    public Task RetryFailedAsync() => PrintAsync(failedOnly: true);

    public async Task<IReadOnlyList<KitchenPrintBatchRecord>> GetFailedAsync() =>
        await _restaurant.GetKitchenPrintBatchesAsync(failedOnly: true);

    public async Task<IReadOnlyList<RestaurantItemChangeNotice>> GetFailedNoticesAsync() =>
        await _restaurant.GetItemChangeNoticesAsync(failedOnly: true);

    /// <summary>
    /// Reimprime cada envio fornecido na impressora da cozinha, sem alterar estado ou numeração.
    /// Não depende de <see cref="Enabled"/>; falhas são propagadas ao chamador.
    /// </summary>
    public async Task PrintKitchenCopiesAsync(
        RestaurantOrderRecord order,
        IReadOnlyList<KitchenPrintBatchRecord> batches)
    {
        await _gate.WaitAsync();
        try
        {
            var printer = await _accessControl.GetSettingAsync("printer.kitchen") ?? "Padrão do Windows";
            KitchenTicketPrinter.PrintKitchenCopies(order, batches, printer);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Imprime a conferência na impressora configurada para o balcão.
    /// </summary>
    public async Task PrintCounterCheckAsync(
        RestaurantOrderRecord order,
        IReadOnlyList<RestaurantOrderItemRecord> selectedItems,
        int tableNumber,
        string areaName)
    {
        await _gate.WaitAsync();
        try
        {
            var printer = await _accessControl.GetSettingAsync("printer.bar") ?? "Padrão do Windows";
            KitchenTicketPrinter.PrintCounterCheck(order, selectedItems, tableNumber, areaName, printer);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PrintAsync(bool failedOnly)
    {
        await _gate.WaitAsync();
        try
        {
            var kitchenPrinter = await _accessControl.GetSettingAsync("printer.kitchen") ?? "Padrão do Windows";
            var barPrinter = await _accessControl.GetSettingAsync("printer.bar") ?? "Padrão do Windows";
            DebugLog.Write($"[KitchenPrint] Impressoras: cozinha='{kitchenPrinter}', bar='{barPrinter}'");

            var failedNotices = await _restaurant.GetItemChangeNoticesAsync(failedOnly: true);
            var notices = failedOnly ? failedNotices :
                failedNotices.Count == 0 ? await _restaurant.GetItemChangeNoticesAsync(failedOnly: false) : [];
            foreach (var notice in notices)
            {
                try
                {
                    if (notice.PrintTarget is not ("kitchen" or "bar"))
                        throw new InvalidOperationException("Destino de impressão do aviso é inválido.");
                    KitchenTicketPrinter.PrintItemChangeNotice(notice, notice.PrintTarget == "bar" ? barPrinter : kitchenPrinter);
                    await _restaurant.MarkItemChangeNoticeAsync(notice.CommandId, printed: true);
                }
                catch (Exception exception)
                {
                    DebugLog.Write($"[KitchenPrint] ERRO aviso: {exception.Message}");
                    await _restaurant.MarkItemChangeNoticeAsync(notice.CommandId, printed: false, exception.Message);
                    break;
                }
            }

            var batches = await _restaurant.GetKitchenPrintBatchesAsync(failedOnly);
            DebugLog.Write($"[KitchenPrint] Lotes pendentes: {batches.Count}");

            foreach (var batch in batches)
            {
                DebugLog.Write($"[KitchenPrint] Processando mesa {batch.TableNumber} com {batch.Items.Count} itens");
                // Separa itens por destino de impressão
                var kitchenItems = batch.Items.Where(i => i.PrintTarget == "kitchen").ToList();
                var barItems = batch.Items.Where(i => i.PrintTarget == "bar").ToList();
                if (kitchenItems.Count + barItems.Count != batch.Items.Count)
                {
                    await _restaurant.MarkKitchenPrintAsync(
                        batch.Items.Where(i => i.PrintTarget is not ("kitchen" or "bar")).Select(i => i.Id),
                        printed: false, "Destino de impressão inválido no cardápio.");
                }
                DebugLog.Write($"[KitchenPrint] Cozinha: {kitchenItems.Count}, Bar: {barItems.Count}");

                // Numeração do pedido: um número único por envio, compartilhado entre cozinha e bar.
                var printNumber = batch.PrintBatchNumber
                    ?? await _restaurant.AssignPrintBatchNumberAsync(batch.Items.Select(item => item.Id));
                var numberedBatch = batch with { PrintBatchNumber = printNumber };

                if (kitchenItems.Count > 0)
                {
                    try
                    {
                        DebugLog.Write($"[KitchenPrint] Imprimindo {kitchenItems.Count} itens na cozinha (pedido Nº {printNumber})...");
                        KitchenTicketPrinter.Print(numberedBatch with { Items = kitchenItems }, showDialog: false, "kitchen", kitchenPrinter);
                        await _restaurant.MarkKitchenPrintAsync(kitchenItems.Select(item => item.Id), printed: true, printTarget: "kitchen");
                        DebugLog.Write("[KitchenPrint] Cozinha OK");
                    }
                    catch (Exception exception)
                    {
                        DebugLog.Write($"[KitchenPrint] ERRO cozinha: {exception.Message}");
                        await _restaurant.MarkKitchenPrintAsync(
                            kitchenItems.Select(item => item.Id), printed: false, exception.Message);
                    }
                }

                if (barItems.Count > 0)
                {
                    try
                    {
                        DebugLog.Write($"[KitchenPrint] Imprimindo {barItems.Count} itens no bar (pedido Nº {printNumber})...");
                        KitchenTicketPrinter.Print(numberedBatch with { Items = barItems }, showDialog: false, "bar", barPrinter);
                        await _restaurant.MarkKitchenPrintAsync(barItems.Select(item => item.Id), printed: true, printTarget: "bar");
                        DebugLog.Write("[KitchenPrint] Bar OK");
                    }
                    catch (Exception exception)
                    {
                        DebugLog.Write($"[KitchenPrint] ERRO bar: {exception.Message}");
                        await _restaurant.MarkKitchenPrintAsync(
                            barItems.Select(item => item.Id), printed: false, exception.Message);
                    }
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }
    
}
