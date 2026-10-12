using System.Windows;
using System.Windows.Input;
using SYSMR.Application.Models;

namespace SYSMR.UI;

public partial class SellerEditorWindow : Window
{
    private readonly SellerRecord? _seller;

    public SellerEditorWindow(SellerRecord? seller)
    {
        InitializeComponent();
        _seller = seller;
        if (seller is not null)
        {
            TitleText.Text = "Editar vendedor";
            NameBox.Text = seller.Name;
        }
        Loaded += (_, _) => NameBox.Focus();
    }

    public SellerRecord? Result { get; private set; }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length < 2)
        {
            MessageBox.Show("Informe um nome com pelo menos 2 caracteres.", "Nome inválido",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            NameBox.Focus();
            return;
        }

        Result = new SellerRecord(_seller?.Id ?? Guid.NewGuid(), name, _seller?.IsActive ?? true);
        DialogResult = true;
    }

    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        Save_Click(sender, new RoutedEventArgs());
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
