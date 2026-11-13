using System.Globalization;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SYSMR.Application.Models;
using SYSMR.Infrastructure;
using SYSMR.Infrastructure.Printing;

namespace SYSMR.UI;

/// <summary>
/// Monta e envia o pedido de cozinha para bobina de 80 mm.
/// Quando uma impressora está configurada, imprime em ESC/POS nativo (texto nítido,
/// corte automático); o diálogo do Windows continua disponível para impressão manual.
/// </summary>
public static class KitchenTicketPrinter
{
    /// <summary>
    /// Imprime uma cópia por envio informado, na impressora indicada, sem modificar a comanda
    /// nem criar numeração. Os itens de cada lote são impressos exatamente como recebidos.
    /// </summary>
    public static void PrintKitchenCopies(
        RestaurantOrderRecord order,
        IReadOnlyList<KitchenPrintBatchRecord> batches,
        string? printerName)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(batches);
        if (batches.Any(batch => batch.OrderId != order.Id))
            throw new ArgumentException("Todos os lotes devem pertencer à comanda informada.", nameof(batches));

        foreach (var batch in batches)
            PrintEscPos(batch, "kitchen", printerName, isManualCopy: true);
    }

    /// <summary>
    /// Imprime uma conferência não fiscal dos itens e quantidades selecionados;
    /// taxa, pagamento e saldo são da comanda inteira.
    /// </summary>
    public static void PrintCounterCheck(
        RestaurantOrderRecord order,
        IReadOnlyList<RestaurantOrderItemRecord> selectedItems,
        int tableNumber,
        string areaName,
        string? printerName)
    {
        var bytes = RestaurantCounterTicket.Build(order, selectedItems, tableNumber, areaName, DateTime.Now);
        EscPosPrinter.SendAsync(ResolveQueueName(printerName), bytes).GetAwaiter().GetResult();
    }

    public static void PrintItemChangeNotice(RestaurantItemChangeNotice notice, string? printerName)
    {
        var title = notice.Action switch
        {
            "remove" => "CANCELAMENTO DE ITEM",
            "observation" => "ALTERACAO DE OBSERVACAO",
            _ => throw new InvalidOperationException("Tipo de aviso de produção inválido.")
        };
        var builder = new EscPosBuilder();
        builder.AlignCenter().Bold(true).Line(title).Bold(false)
            .Line(notice.AreaName).Line("MESA").DoubleSize(true)
            .Line(notice.TableNumber.ToString("00", CultureInfo.InvariantCulture))
            .DoubleSize(false).AlignLeft()
            .Line(notice.PrintBatchNumber is null ? "Pedido original: sem número" : $"Pedido original Nº {notice.PrintBatchNumber}")
            .Line($"Alterado em: {notice.CreatedAt.ToLocalTime():dd/MM/yyyy HH:mm}")
            .Separator().Emphasized(true).WrappedLine($"1x {notice.ItemName}").Emphasized(false)
            .WrappedLine($"Observação anterior: {(notice.OldObservation.Length == 0 ? "(sem observação)" : notice.OldObservation)}");
        if (notice.Action == "observation")
            builder.Bold(true).WrappedLine($"Nova observação: {(notice.NewObservation.Length == 0 ? "(sem observação)" : notice.NewObservation)}").Bold(false);
        builder.Separator().Feed(3).Cut();
        EscPosPrinter.SendAsync(ResolveQueueName(printerName), builder.Build()).GetAwaiter().GetResult();
    }

    public static bool Print(KitchenPrintBatchRecord batch, bool showDialog, string target = "kitchen", string? printerName = null)
    {
        if (!showDialog)
        {
            PrintEscPos(batch, target, printerName);
            return true;
        }

        var items = batch.Items;
        var printDialog = new PrintDialog();
        if (!string.IsNullOrWhiteSpace(printerName) && printerName != "Padrão do Windows" &&
            !EscPosPrinter.TryParseNetworkTarget(printerName, out _, out _))
            printDialog.PrintQueue = new System.Printing.PrintQueue(
                new System.Printing.PrintServer(), printerName);
        if (printDialog.ShowDialog() != true)
            return false;

        const double millimetersToDip = 96d / 25.4d;
        const double rollWidthMillimeters = 80;
        const double pagePadding = 12;
        const double quantityColumnWidth = 42;
        var pageWidth = rollWidthMillimeters * millimetersToDip;
        var contentWidth = pageWidth - pagePadding * 2;
        var itemNameWidth = contentWidth - quantityColumnWidth - 12;
        var estimatedItemsHeight = items.Sum(item =>
        {
            var nameLines = Math.Max(1, (int)Math.Ceiling(item.Name.Length / 23d));
            var observationLines = string.IsNullOrWhiteSpace(item.Observation)
                ? 0
                : Math.Max(1, (int)Math.Ceiling(item.Observation.Length / 28d));
            return 30 + nameLines * 15 + observationLines * 13;
        });
        var pageHeight = Math.Max(380, 300 + estimatedItemsHeight);
        printDialog.PrintTicket ??= new PrintTicket();
        printDialog.PrintTicket.PageMediaSize = new PageMediaSize(
            PageMediaSizeName.Unknown,
            pageWidth,
            pageHeight);
        var ink = new SolidColorBrush(Color.FromRgb(23, 35, 59));
        var accent = new SolidColorBrush(Color.FromRgb(24, 169, 153));
        var muted = new SolidColorBrush(Color.FromRgb(104, 119, 143));
        var separator = new SolidColorBrush(Color.FromRgb(222, 228, 236));
        var document = new FlowDocument
        {
            PagePadding = new Thickness(pagePadding),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 10,
            PageWidth = pageWidth,
            PageHeight = pageHeight,
            ColumnWidth = contentWidth,
            TextAlignment = TextAlignment.Left
        };
        document.Blocks.Add(new BlockUIContainer(new Image
        {
            Source = LoadTicketIcon(),
            Width = 48,
            Height = 48,
            HorizontalAlignment = HorizontalAlignment.Left
        }) { Margin = new Thickness(0, 0, 0, 4) });

        var title = target == "bar" ? "PEDIDO DE BEBIDA" : "PEDIDO DE COZINHA";
        var headerTable = new Table { CellSpacing = 0 };
        headerTable.Columns.Add(new TableColumn { Width = new GridLength(contentWidth * 0.42) });
        headerTable.Columns.Add(new TableColumn { Width = new GridLength(contentWidth * 0.58) });
        var headerRows = new TableRowGroup();
        var headerRow = new TableRow();
        var orderNumberCell = new TableCell { Padding = new Thickness(0, 0, 0, 4) };
        orderNumberCell.Blocks.Add(new Paragraph(new Run(title))
        {
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            Foreground = muted,
            Margin = new Thickness(0, 0, 0, 2)
        });
        orderNumberCell.Blocks.Add(new Paragraph(new Run(
            batch.PrintBatchNumber is not null ? $"Nº {batch.PrintBatchNumber}" :
            batch.DailyNumber is null ? "Nº —" : $"Nº {batch.DailyNumber}"))
        {
            FontSize = 26,
            FontWeight = FontWeights.ExtraBold,
            Foreground = ink,
            LineHeight = 28,
            Margin = new Thickness(0)
        });
        var tableCell = new TableCell { Padding = new Thickness(0, 0, 0, 4) };
        tableCell.Blocks.Add(new Paragraph(new Run("MESA"))
        {
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = muted,
            Margin = new Thickness(0),
            TextAlignment = TextAlignment.Center
        });
        tableCell.Blocks.Add(new Paragraph(new Run(batch.TableNumber.ToString(CultureInfo.InvariantCulture)))
        {
            FontSize = 64,
            FontWeight = FontWeights.ExtraBold,
            Foreground = ink,
            LineHeight = 66,
            Margin = new Thickness(0),
            TextAlignment = TextAlignment.Center
        });
        headerRow.Cells.Add(orderNumberCell);
        headerRow.Cells.Add(tableCell);
        headerRows.Rows.Add(headerRow);
        headerTable.RowGroups.Add(headerRows);
        document.Blocks.Add(headerTable);
        document.Blocks.Add(new Paragraph(new Run(batch.AreaName))
        {
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = accent,
            Margin = new Thickness(0, 0, 0, 4),
            TextAlignment = TextAlignment.Center
        });
        document.Blocks.Add(new Paragraph(new Run(
            $"{DateTime.Now:dd/MM/yyyy  HH:mm}   •   Atendente: {(batch.EmployeeName.Length == 0 ? "não informado" : batch.EmployeeName)}\nCliente: {(string.IsNullOrWhiteSpace(batch.CustomerName) ? "não informado" : batch.CustomerName)}"))
        {
            FontSize = 9,
            Foreground = muted,
            BorderBrush = separator,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 0, 0, 6),
            Margin = new Thickness(0, 0, 0, 6),
            TextAlignment = TextAlignment.Center
        });

        var itemsTable = new Table { CellSpacing = 0 };
        itemsTable.Columns.Add(new TableColumn { Width = new GridLength(itemNameWidth) });
        itemsTable.Columns.Add(new TableColumn { Width = new GridLength(quantityColumnWidth) });
        var itemRows = new TableRowGroup();
        foreach (var item in items)
        {
            var row = new TableRow();
            row.Cells.Add(CreateItemCell(item, ink, muted, separator));
            row.Cells.Add(CreateQuantityCell(
                "× " + item.Quantity.ToString("0.##", CultureInfo.GetCultureInfo("pt-BR")), ink));
            itemRows.Rows.Add(row);
        }
        itemsTable.RowGroups.Add(itemRows);
        document.Blocks.Add(itemsTable);
        document.Blocks.Add(new Paragraph(new Run(
            $"{items.Sum(item => item.Quantity):0.##} unidade(s)  •  {items.Count} linha(s)"))
        {
            FontSize = 8,
            Foreground = muted,
            BorderBrush = separator,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 6, 0, 0),
            Margin = new Thickness(0, 6, 0, 0),
            TextAlignment = TextAlignment.Center
        });

        printDialog.PrintDocument(
            ((IDocumentPaginatorSource)document).DocumentPaginator,
            $"Pedido de cozinha SYSMR - Mesa {batch.TableNumber}");
        return true;
    }

    private static TableCell CreateQuantityCell(string text, Brush brush)
    {
        var cell = new TableCell
        {
            Padding = new Thickness(2, 4, 2, 4),
            TextAlignment = TextAlignment.Right,
            BorderBrush = new SolidColorBrush(Color.FromRgb(222, 228, 236)),
            BorderThickness = new Thickness(0, 0, 0, 0.5)
        };
        cell.Blocks.Add(new Paragraph(new Run(text))
        {
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = brush,
            Margin = new Thickness(0),
            TextAlignment = TextAlignment.Right
        });
        return cell;
    }

    private static TableCell CreateItemCell(
        RestaurantOrderItemRecord item,
        Brush itemBrush,
        Brush observationBrush,
        Brush borderBrush)
    {
        var cell = new TableCell
        {
            Padding = new Thickness(2, 4, 2, 4),
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(0, 0, 0, 0.5)
        };
        cell.Blocks.Add(new Paragraph(new Run(item.Name))
        {
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = itemBrush,
            Margin = new Thickness(0)
        });
        if (!string.IsNullOrWhiteSpace(item.Observation))
        {
            cell.Blocks.Add(new Paragraph(new Run($"Obs.: {item.Observation}"))
            {
                FontSize = 9,
                Foreground = observationBrush,
                Margin = new Thickness(0, 2, 0, 0)
            });
        }
        return cell;
    }

    /// <summary>
    /// Imprime o ticket em ESC/POS nativo: texto nítido (sem dithering), mesa em destaque
    /// e corte automático. Aceita fila do Windows, "Padrão do Windows" ou "IP:porta" (rede, 9100).
    /// </summary>
    private static void PrintEscPos(KitchenPrintBatchRecord batch, string target, string? printerName, bool isManualCopy = false)
    {
        var queue = ResolveQueueName(printerName);
        var builder = new EscPosBuilder();

        var icon = new FormatConvertedBitmap(LoadTicketIcon(), PixelFormats.Bgra32, null, 0);
        var width = icon.PixelWidth;
        var height = icon.PixelHeight;
        var pixels = new byte[width * height * 4];
        icon.CopyPixels(pixels, width * 4, 0);
        var stride = (width + 7) / 8;
        var raster = new byte[stride * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var offset = (y * width + x) * 4;
            var alpha = pixels[offset + 3];
            var brightness = (pixels[offset] * 11 + pixels[offset + 1] * 59 + pixels[offset + 2] * 30) / 100;
            if (alpha >= 128 && brightness < 180)
                raster[y * stride + x / 8] |= (byte)(0x80 >> (x % 8));
        }
        builder.AlignLeft().RasterBitmap(width, height, raster).Line();
        builder.AlignCenter().Bold(true)
            .Line(isManualCopy ? "COPIA DE COZINHA" : target == "bar" ? "PEDIDO DE BEBIDA" : "PEDIDO DE COZINHA");
        if (isManualCopy)
            builder.Line("NAO E NOVO PEDIDO");
        builder.Bold(false)
            .Line(batch.AreaName)
            .Line()
            .Line("MESA")
            .DoubleSize(true)
            .Line(batch.TableNumber.ToString("00", CultureInfo.InvariantCulture))
            .DoubleSize(false)
            .Line();

        builder.AlignLeft();
        if (isManualCopy)
        {
            builder.Line(batch.DailyNumber is null ? "Comanda: sem numero" : $"Comanda Nº {batch.DailyNumber}")
                .Line(batch.PrintBatchNumber is null ? "Envio: sem numero" : $"PEDIDO Nº {batch.PrintBatchNumber}")
                .Line($"Data: {DateTime.Now:dd/MM/yyyy HH:mm}");
        }
        else
        {
            builder.Line(batch.PrintBatchNumber is not null
                ? $"PEDIDO Nº {batch.PrintBatchNumber}   {DateTime.Now:dd/MM/yyyy HH:mm}"
                : batch.DailyNumber is null
                    ? $"Data: {DateTime.Now:dd/MM/yyyy HH:mm}"
                    : $"Nº {batch.DailyNumber}   {DateTime.Now:dd/MM/yyyy HH:mm}");
        }
        builder
            .Line($"Atendente: {(batch.EmployeeName.Length == 0 ? "não informado" : batch.EmployeeName)}")
            .WrappedLine($"Cliente: {(string.IsNullOrWhiteSpace(batch.CustomerName) ? "não informado" : batch.CustomerName)}")
            .Separator();

        foreach (var item in batch.Items)
        {
            builder.Emphasized(true)
                .WrappedLine($"{item.Quantity.ToString(isManualCopy ? "0.############################" : "0.##", CultureInfo.GetCultureInfo("pt-BR"))}x {item.Name}", EscPosBuilder.LineWidth80Mm - 2)
                .Emphasized(false);
            if (!string.IsNullOrWhiteSpace(item.Observation))
                builder.Bold(true)
                    .WrappedLine($"Obs.: {item.Observation}", EscPosBuilder.LineWidth80Mm - 3, "    ")
                    .Bold(false);
            builder.Line();
        }

        builder.Separator()
            .AlignCenter()
            .Line($"{batch.Items.Sum(item => item.Quantity).ToString(isManualCopy ? "0.############################" : "0.##", CultureInfo.CurrentCulture)} unidade(s) - {batch.Items.Count} linha(s)")
            .Feed(3)
            .Cut();

        EscPosPrinter.SendAsync(queue, builder.Build()).GetAwaiter().GetResult();
    }

    private static BitmapSource LoadTicketIcon()
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri("pack://application:,,,/SYSMR.UI;component/Assets/sysmr-print.png");
        image.DecodePixelWidth = 64;
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static string ResolveQueueName(string? printerName)
    {
        if (!string.IsNullOrWhiteSpace(printerName) && printerName != "Padrão do Windows")
            return printerName;
        using var server = new LocalPrintServer();
        return server.DefaultPrintQueue?.Name
            ?? throw new InvalidOperationException(
                "Nenhuma impressora padrão configurada no Windows. Escolha a impressora em Configurações.");
    }

    /// <summary>
    /// Imprime um cartão com o QR Code do webapp do garçom para o balcão.
    /// Aceita fila do Windows, "Padrão do Windows" ou "IP:porta" (rede, 9100).
    /// </summary>
    public static async Task PrintWaiterQrCodeAsync(
        string url,
        string storeName,
        string? printerName,
        CancellationToken cancellationToken = default)
    {
        var queue = ResolveQueueName(printerName);
        DebugLog.Write($"[QRCode] Imprimindo QR para '{url}' na fila '{queue}'...");
        var builder = new EscPosBuilder();

        builder.AlignCenter().Bold(true)
            .Line("SYSMR - GARCONS")
            .Bold(false)
            .Line(storeName)
            .Separator()
            .Line("Aponte a camera do celular")
            .Line("para abrir o app do garcom:")
            .Line()
            .QrCodeBitmap(url)
            .Line()
            .Line(url)
            .Line()
            .Line("Conecte o celular no Wi-Fi da loja.")
            .Line("Funciona mesmo sem internet.")
            .Feed(3)
            .Cut();

        await EscPosPrinter.SendAsync(queue, builder.Build(), cancellationToken);
        DebugLog.Write("[QRCode] QR Code enviado com sucesso.");
    }
}
