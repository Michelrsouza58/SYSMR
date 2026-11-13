using System.ComponentModel;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace SYSMR.Infrastructure.Printing;

/// <summary>
/// Monta comandos ESC/POS para impressoras térmicas (Daruma, Bematech, Elgin, Epson, POS80 e clones).
/// O texto é impresso de forma nativa pela impressora (nítido, sem dithering), com suporte a
/// acentos em português via code page PC850.
/// </summary>
public sealed class EscPosBuilder
{
    public const int LineWidth80Mm = 48;

    private static readonly Encoding CodePage = CreateCodePage();
    private readonly List<byte> _bytes = [];

    private static Encoding CreateCodePage()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(850);
    }

    public EscPosBuilder()
    {
        _bytes.AddRange([0x1B, 0x40]);        // ESC @ — inicializa
        _bytes.AddRange([0x1B, 0x74, 0x02]);  // ESC t 2 — code page PC850 (acentos pt-BR)
    }

    /// <summary>0 = esquerda, 1 = centro, 2 = direita.</summary>
    public EscPosBuilder Align(byte alignment)
    {
        _bytes.AddRange([0x1B, 0x61, alignment]);
        return this;
    }

    public EscPosBuilder AlignLeft() => Align(0);
    public EscPosBuilder AlignCenter() => Align(1);

    public EscPosBuilder RasterBitmap(int width, int height, byte[] packedPixels)
    {
        var widthBytes = (width + 7) / 8;
        if (width <= 0 || width > 576 || height <= 0 || height > 2048 ||
            packedPixels.Length != widthBytes * height)
            throw new ArgumentException("Dimensões inválidas para a imagem de impressão.");
        _bytes.AddRange([0x1D, 0x76, 0x30, 0x00,
            (byte)(widthBytes & 0xFF), (byte)(widthBytes >> 8),
            (byte)(height & 0xFF), (byte)(height >> 8)]);
        _bytes.AddRange(packedPixels);
        return this;
    }

    public EscPosBuilder Bold(bool enabled)
    {
        _bytes.AddRange([0x1B, 0x45, enabled ? (byte)1 : (byte)0]);
        return this;
    }

    /// <summary>Dobra largura e altura do texto (ideal para número de mesa).</summary>
    public EscPosBuilder DoubleSize(bool enabled)
    {
        // GS ! n — n = 0x33 dobra largura+altura (mais compatível que 0x11 em clones POS80)
        _bytes.AddRange([0x1D, 0x21, enabled ? (byte)0x33 : (byte)0x00]);
        return this;
    }

    /// <summary>Texto em negrito com altura dobrada (itens e observações legíveis na cozinha).</summary>
    public EscPosBuilder Emphasized(bool enabled)
    {
        _bytes.AddRange([0x1D, 0x21, enabled ? (byte)0x08 : (byte)0x00]); // altura 2x
        _bytes.AddRange([0x1B, 0x45, enabled ? (byte)1 : (byte)0]);       // negrito
        return this;
    }

    public EscPosBuilder Line(string? text = null)
    {
        if (!string.IsNullOrEmpty(text))
            _bytes.AddRange(CodePage.GetBytes(text));
        _bytes.Add(0x0A);
        return this;
    }

    /// <summary>Escreve o texto quebrando em várias linhas no limite da bobina.</summary>
    public EscPosBuilder WrappedLine(string text, int width = LineWidth80Mm, string? continuationIndent = null)
    {
        foreach (var line in Wrap(text, width, continuationIndent))
            Line(line);
        return this;
    }

    public EscPosBuilder Separator(int width = LineWidth80Mm) => Line(new string('-', width));

    public EscPosBuilder Feed(int lines = 1)
    {
        _bytes.AddRange([0x1B, 0x64, (byte)Math.Clamp(lines, 0, 255)]);
        return this;
    }

    /// <summary>Avança o papel e aciona a guilhotina (corte parcial). Inofensivo sem guilhotina.</summary>
    public EscPosBuilder Cut()
    {
        _bytes.AddRange([0x1D, 0x56, 0x42, 0x03]);
        return this;
    }

    /// <summary>
    /// Imprime um QR Code usando o comando nativo GS ( k (suportado por Epson, Elgin,
    /// Bematech, Daruma e clones POS80). O conteúdo é tipicamente uma URL.
    /// </summary>
    public EscPosBuilder QrCode(string content, int moduleSize = 6)
    {
        var data = CodePage.GetBytes(content);
        if (data.Length > 7089)
            throw new ArgumentOutOfRangeException(nameof(content), "Conteúdo do QR Code é grande demais.");

        // GS ( k — função 65: modelo 2 (QR Code padrão)
        _bytes.AddRange([0x1D, 0x28, 0x6B, 0x04, 0x00, 0x31, 0x41, 0x32, 0x00]);
        // Função 67: tamanho do módulo (1–16; 6 ≈ bom para 80 mm)
        _bytes.AddRange([0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x43, (byte)Math.Clamp(moduleSize, 1, 16)]);
        // Função 69: correção de erro nível M (51)
        _bytes.AddRange([0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x45, 0x31]);
        // Função 80: armazena os dados
        var storeLength = data.Length + 3;
        _bytes.AddRange([0x1D, 0x28, 0x6B, (byte)(storeLength & 0xFF), (byte)(storeLength >> 8), 0x31, 0x50, 0x30]);
        _bytes.AddRange(data);
        // Função 81: imprime o QR Code
        _bytes.AddRange([0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x51, 0x30]);
        return this;
    }

    /// <summary>
    /// Imprime um QR Code como imagem bitmap (GS v 0). Mais compatível que o comando
    /// nativo GS ( k em clones POS80 que não implementam QR por firmware.
    /// </summary>
    public EscPosBuilder QrCodeBitmap(string content, int pixelScale = 4)
    {
        using var generator = new QRCoder.QRCodeGenerator();
        using var qrData = generator.CreateQrCode(content, QRCoder.QRCodeGenerator.ECCLevel.M);
        var matrix = qrData.ModuleMatrix;
        var modules = matrix.Count;
        var quietZone = 4; // margem branca obrigatória ao redor do QR
        var size = (modules + quietZone * 2) * pixelScale;
        var widthBytes = (size + 7) / 8;

        // Centraliza: bobina de 80 mm a 203 DPI ≈ 576 dots de largura (72 bytes).
        var leftMarginBytes = Math.Max(0, (72 - widthBytes) / 2);
        var totalWidthBytes = widthBytes + leftMarginBytes;

        for (var y = 0; y < size; y++)
        {
            _bytes.AddRange([0x1D, 0x76, 0x30, 0x00,
                (byte)(totalWidthBytes & 0xFF), (byte)(totalWidthBytes >> 8),
                0x01, 0x00]); // 1 linha por faixa
            for (var pad = 0; pad < leftMarginBytes; pad++)
                _bytes.Add(0x00);
            for (var xByte = 0; xByte < widthBytes; xByte++)
            {
                byte packed = 0;
                for (var bit = 0; bit < 8; bit++)
                {
                    var x = xByte * 8 + bit;
                    if (x >= size)
                        break;
                    var moduleX = x / pixelScale - quietZone;
                    var moduleY = y / pixelScale - quietZone;
                    var dark = moduleX >= 0 && moduleY >= 0 &&
                               moduleX < modules && moduleY < modules &&
                               matrix[moduleY][moduleX];
                    if (dark)
                        packed |= (byte)(0x80 >> bit);
                }
                _bytes.Add(packed);
            }
        }
        return this;
    }

    public byte[] Build() => [.. _bytes];

    public static IReadOnlyList<string> Wrap(string text, int width, string? continuationIndent = null)
    {
        var lines = new List<string>();
        var indent = continuationIndent ?? string.Empty;
        var current = string.Empty;
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (candidate.Length <= width)
            {
                current = candidate;
                continue;
            }
            if (current.Length > 0)
                lines.Add(current);
            current = indent + word;
            while (current.Length > width)
            {
                lines.Add(current[..width]);
                current = indent + current[width..];
            }
        }
        if (current.Length > 0)
            lines.Add(current);
        return lines.Count == 0 ? [string.Empty] : lines;
    }
}

/// <summary>
/// Envia bytes ESC/POS crus para a impressora: fila do Windows (USB/serial instalada com driver)
/// via spooler em modo RAW, ou direto pela rede via TCP/IP (porta 9100).
/// </summary>
public static partial class EscPosPrinter
{
    [GeneratedRegex(@"^(.+):(\d{4,5})$")]
    private static partial Regex HostPortRegex();

    /// <summary>Detecta destino no formato "192.168.0.50:9100" para impressão direta pela rede.</summary>
    public static bool TryParseNetworkTarget(string? target, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        if (string.IsNullOrWhiteSpace(target))
            return false;
        var match = HostPortRegex().Match(target.Trim());
        if (!match.Success)
            return false;
        host = match.Groups[1].Value;
        port = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        return port is >= 1 and <= 65535;
    }

    public static async Task SendAsync(string target, byte[] data, CancellationToken cancellationToken = default)
    {
        if (TryParseNetworkTarget(target, out var host, out var port))
        {
            await SendTcpAsync(host, port, data, cancellationToken);
            return;
        }
        SendToWindowsQueue(target, data);
    }

    public static async Task SendTcpAsync(
        string host, int port, byte[] data, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, cancellationToken);
            await client.GetStream().WriteAsync(data, cancellationToken);
        }
        catch (Exception exception) when (exception is SocketException or IOException or OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Não foi possível imprimir em {host}:{port}. Confira se a impressora está ligada e na mesma rede.",
                exception);
        }
    }

    public static void SendToWindowsQueue(string queueName, byte[] data)
    {
        if (string.IsNullOrWhiteSpace(queueName))
            throw new InvalidOperationException("Nenhuma impressora foi informada.");

        if (!OpenPrinter(queueName, out var handle, IntPtr.Zero))
            throw new InvalidOperationException(
                $"A impressora '{queueName}' não foi encontrada no Windows. Confira o nome em Configurações.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        try
        {
            var docInfo = new DocInfoA { DocName = "SYSMR", DataType = "RAW" };
            if (!StartDocPrinter(handle, 1, docInfo))
                throw new InvalidOperationException(
                    $"A impressora '{queueName}' recusou o trabalho de impressão.",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            try
            {
                if (!StartPagePrinter(handle))
                    throw new InvalidOperationException(
                        $"Não foi possível iniciar a página na impressora '{queueName}'.",
                        new Win32Exception(Marshal.GetLastWin32Error()));
                if (!WritePrinter(handle, data, data.Length, out var written) || written != data.Length)
                    throw new InvalidOperationException(
                        $"A impressora '{queueName}' não recebeu todos os dados. Verifique o cabo e o driver.",
                        new Win32Exception(Marshal.GetLastWin32Error()));
                EndPagePrinter(handle);
            }
            finally
            {
                EndDocPrinter(handle);
            }
        }
        finally
        {
            ClosePrinter(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private sealed class DocInfoA
    {
        [MarshalAs(UnmanagedType.LPStr)] public string DocName = "SYSMR";
        [MarshalAs(UnmanagedType.LPStr)] public string? OutputFile;
        [MarshalAs(UnmanagedType.LPStr)] public string DataType = "RAW";
    }

    [DllImport("winspool.Drv", EntryPoint = "OpenPrinterA", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern bool OpenPrinter(string printerName, out IntPtr handle, IntPtr defaults);

    [DllImport("winspool.Drv", EntryPoint = "StartDocPrinterA", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern bool StartDocPrinter(IntPtr handle, int level, [In] DocInfoA docInfo);

    [DllImport("winspool.Drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr handle);

    [DllImport("winspool.Drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr handle, byte[] bytes, int count, out int written);

    [DllImport("winspool.Drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr handle);

    [DllImport("winspool.Drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr handle);

    [DllImport("winspool.Drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr handle);
}
