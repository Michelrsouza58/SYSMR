using System.Globalization;
using System.Speech.Recognition;
using System.Windows;
using System.Windows.Controls;
using SYSMR.Application.Services;
using SYSMR.Domain.Entities;

namespace SYSMR.UI;

public partial class ProductEditorWindow : Window
{
    private readonly Product? _product;
    private SpeechRecognitionEngine? _speechEngine;

    public ProductEditorWindow(Product? product)
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            if (AutoStartVoice)
                StartVoiceRecognition();
        };
        Closed += (_, _) => StopVoiceRecognition();
        _product = product;
        if (product is null)
            return;

        TitleText.Text = "Editar produto";
        NameBox.Text = product.Name;
        BarcodeBox.Text = product.Barcode;
        UnitCombo.Text = product.UnitOfMeasure;
        CostBox.Text = product.CostPrice.ToString(CultureInfo.CurrentCulture);
        PriceBox.Text = product.SalePrice.ToString(CultureInfo.CurrentCulture);
        StockBox.Text = product.Stock.ToString(CultureInfo.CurrentCulture);
        MinimumStockBox.Text = product.MinimumStock.ToString(CultureInfo.CurrentCulture);
        CategoryBox.Text = product.Category;
        ScaleCodeBox.Text = product.ScaleCode;
        SoldByWeightCheck.IsChecked = product.SoldByWeight;
    }

    public Product? Result { get; private set; }
    public bool AutoStartVoice { get; set; }

    private void StartVoice_Click(object sender, RoutedEventArgs e) => StartVoiceRecognition();

    private void StartVoiceRecognition()
    {
        StopVoiceRecognition();
        try
        {
            var recognizerInfo = SpeechRecognitionEngine.InstalledRecognizers()
                .FirstOrDefault(info => info.Culture.Name.Equals("pt-BR", StringComparison.OrdinalIgnoreCase));
            if (recognizerInfo is null)
            {
                ErrorText.Text = "O reconhecimento de voz em português (Brasil) não está instalado. Adicione o recurso de fala pt-BR no Windows. O áudio não é enviado à internet.";
                return;
            }

            _speechEngine = new SpeechRecognitionEngine(recognizerInfo);
            _speechEngine.LoadGrammar(new DictationGrammar());
            _speechEngine.SetInputToDefaultAudioDevice();
            _speechEngine.SpeechRecognized += SpeechRecognized;
            _speechEngine.RecognizeCompleted += SpeechRecognitionCompleted;
            ErrorText.Text = "Escutando pelo microfone local. Fale o valor do campo selecionado; o áudio não sai deste computador.";
            _speechEngine.RecognizeAsync(RecognizeMode.Single);
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or ArgumentException
                                          or PlatformNotSupportedException
                                          or UnauthorizedAccessException
                                          or System.Runtime.InteropServices.COMException)
        {
            StopVoiceRecognition();
            ErrorText.Text = $"Não foi possível iniciar o reconhecimento local: {exception.Message}";
        }
    }

    private void SpeechRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        if (e.Result.Confidence < 0.45f)
            return;

        _ = Dispatcher.InvokeAsync(() =>
        {
            if (VoiceFieldCombo.SelectedIndex == 0)
                NameBox.Text = e.Result.Text;
            else if (VoiceFieldCombo.SelectedIndex == 1)
                BarcodeBox.Text = PortugueseSpeechParser.TryParseDigits(e.Result.Text, out var barcode)
                    ? barcode
                    : e.Result.Text;
            else if (VoiceFieldCombo.SelectedIndex == 2)
            {
                UnitCombo.Text = NormalizeUnit(e.Result.Text);
                SoldByWeightCheck.IsChecked = UnitCombo.Text == "KG";
            }
            else if (VoiceFieldCombo.SelectedIndex == 3)
                CostBox.Text = FormatSpokenNumber(e.Result.Text);
            else if (VoiceFieldCombo.SelectedIndex == 4)
                PriceBox.Text = FormatSpokenNumber(e.Result.Text);
            else if (VoiceFieldCombo.SelectedIndex == 5)
                StockBox.Text = FormatSpokenNumber(e.Result.Text);
            else if (VoiceFieldCombo.SelectedIndex == 6)
                MinimumStockBox.Text = FormatSpokenNumber(e.Result.Text);
            else if (VoiceFieldCombo.SelectedIndex == 7)
                CategoryBox.Text = e.Result.Text;
            else if (VoiceFieldCombo.SelectedIndex == 8)
                ScaleCodeBox.Text = PortugueseSpeechParser.TryParseDigits(e.Result.Text, out var scaleCode)
                    ? scaleCode
                    : e.Result.Text;

            ErrorText.Text = $"Reconhecido: {e.Result.Text}. Confira o valor antes de salvar.";
        });
    }

    private static string FormatSpokenNumber(string text) =>
        PortugueseSpeechParser.TryParseNumber(text, out var value)
            ? value.ToString(CultureInfo.CurrentCulture)
            : text;

    private static string NormalizeUnit(string text)
    {
        var normalized = text.Trim().ToUpperInvariant();
        return normalized switch
        {
            "UNIDADE" or "UNIDADES" => "UN",
            "QUILO" or "QUILOS" or "QUILOGRAMA" or "QUILOGRAMAS" => "KG",
            "LITRO" or "LITROS" => "L",
            "MILILITRO" or "MILILITROS" => "ML",
            "CAIXA" or "CAIXAS" => "CX",
            "PACOTE" or "PACOTES" => "PCT",
            _ => normalized
        };
    }

    private void SpeechRecognitionCompleted(object? sender, RecognizeCompletedEventArgs e)
    {
        if (e.Error is not null)
            _ = Dispatcher.InvokeAsync(() => ErrorText.Text = $"Falha no reconhecimento: {e.Error.Message}");
        else if (e.Result is null && !e.Cancelled)
            _ = Dispatcher.InvokeAsync(() => ErrorText.Text = "Não foi possível reconhecer a fala. Tente novamente em ambiente silencioso.");
        StopVoiceRecognition();
    }

    private void StopVoiceRecognition()
    {
        var engine = _speechEngine;
        _speechEngine = null;
        if (engine is null)
            return;
        engine.SpeechRecognized -= SpeechRecognized;
        engine.RecognizeCompleted -= SpeechRecognitionCompleted;
        try
        {
            if (engine.AudioState != AudioState.Stopped)
                engine.RecognizeAsyncCancel();
        }
        catch (InvalidOperationException exception)
        {
            if (IsLoaded)
                ErrorText.Text = $"A escuta de voz já havia terminado: {exception.Message}";
        }
        engine.Dispose();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;
        try
        {
            if (!TryParseAmount(CostBox.Text, out var cost) ||
                !TryParseAmount(PriceBox.Text, out var price) ||
                !TryParseAmount(StockBox.Text, out var stock) ||
                !TryParseAmount(MinimumStockBox.Text, out var minimumStock))
            {
                ErrorText.Text = "Informe valores numéricos válidos para preço e estoque.";
                return;
            }

            var unit = UnitCombo.Text.Trim().ToUpperInvariant();
            if (unit.Length is < 1 or > 8)
            {
                ErrorText.Text = "Informe uma unidade de medida válida (por exemplo: UN, KG, L, CX).";
                return;
            }

            Result = new Product(
                _product?.Id ?? Guid.NewGuid(),
                NameBox.Text,
                BarcodeBox.Text,
                ScaleCodeBox.Text,
                cost,
                price,
                stock,
                minimumStock,
                CategoryBox.Text,
                _product?.Ncm ?? string.Empty,
                _product?.Cest ?? string.Empty,
                SoldByWeightCheck.IsChecked == true,
                unit);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            ErrorText.Text = exception.Message;
        }
    }

    private static bool TryParseAmount(string text, out decimal value) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out value) ||
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value);

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
