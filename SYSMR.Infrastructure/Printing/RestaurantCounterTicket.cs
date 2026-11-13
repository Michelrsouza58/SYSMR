using System.Globalization;
using SYSMR.Application.Models;

namespace SYSMR.Infrastructure.Printing;

public static class RestaurantCounterTicket
{
    public static byte[] Build(RestaurantOrderRecord order, IReadOnlyList<RestaurantOrderItemRecord> selectedItems,
        int tableNumber, string areaName, DateTime printedAt)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(selectedItems);
        ArgumentNullException.ThrowIfNull(areaName);
        var orderItems = order.Items.ToDictionary(item => item.Id);
        var selectedIds = new HashSet<Guid>();
        var items = new List<RestaurantOrderItemRecord>(selectedItems.Count);
        foreach (var selected in selectedItems)
        {
            if (!selectedIds.Add(selected.Id) || !orderItems.TryGetValue(selected.Id, out var item) ||
                selected.Quantity <= 0 || selected.Quantity > item.Quantity)
                throw new ArgumentException("Os itens selecionados devem pertencer à comanda e ter quantidades válidas.", nameof(selectedItems));
            items.Add(item with { Quantity = selected.Quantity });
        }
        if (items.Count == 0)
            throw new ArgumentException("Selecione ao menos um item.", nameof(selectedItems));

        var culture = CultureInfo.GetCultureInfo("pt-BR");
        var builder = new EscPosBuilder();
        builder.AlignCenter().Bold(true)
            .Line("CONFERENCIA DE BALCAO")
            .Line("NAO E COMPROVANTE DE PAGAMENTO")
            .Bold(false)
            .WrappedLine(areaName)
            .Line($"MESA {tableNumber:00}")
            .AlignLeft()
            .Line(order.DailyNumber is null ? "Comanda: sem numero" : $"Comanda Nº {order.DailyNumber}")
            .Line($"Data: {printedAt:dd/MM/yyyy HH:mm}")
            .WrappedLine($"Atendente: {(string.IsNullOrWhiteSpace(order.EmployeeName) ? "não informado" : order.EmployeeName)}")
            .WrappedLine($"Cliente: {(string.IsNullOrWhiteSpace(order.CustomerName) ? "não informado" : order.CustomerName)}")
            .Separator();
        foreach (var item in items)
        {
            builder.Bold(true).WrappedLine(item.Name).Bold(false)
                .WrappedLine($"Qtd impressa: {item.Quantity.ToString("0.############################", culture)}  Paga na linha: {item.PaidQuantity.ToString("0.############################", culture)}")
                .WrappedLine($"Unit.: {item.UnitPrice.ToString("C", culture)}  Total: {decimal.Round(item.Total, 2, MidpointRounding.AwayFromZero).ToString("C", culture)}");
            if (!string.IsNullOrWhiteSpace(item.Observation))
                builder.WrappedLine($"Obs.: {item.Observation}");
            builder.Line();
        }
        if (order.HasUnallocatedPayments)
            builder.WrappedLine("Ha pagamento por valor nao vinculado aos itens.");
        builder.Separator()
            .WrappedLine($"Soma dos itens selecionados: {decimal.Round(items.Sum(item => item.Total), 2, MidpointRounding.AwayFromZero).ToString("C", culture)}")
            .WrappedLine($"Taxa da comanda: {order.WaiterServiceCharge.ToString("C", culture)}")
            .WrappedLine($"Total da comanda: {order.Total.ToString("C", culture)}")
            .WrappedLine($"Total pago da comanda: {order.PaidTotal.ToString("C", culture)}")
            .WrappedLine($"Saldo da comanda: {order.RemainingTotal.ToString("C", culture)}")
            .AlignCenter().Bold(true).Line("NAO E COMPROVANTE DE PAGAMENTO").Bold(false)
            .Feed(3).Cut();
        return builder.Build();
    }
}
