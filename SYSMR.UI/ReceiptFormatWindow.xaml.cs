using System.Windows;

namespace SYSMR.UI;

public partial class ReceiptFormatWindow : Window
{
    public ReceiptFormatWindow(bool preferA4)
    {
        InitializeComponent();
        A4Option.IsChecked = preferA4;
        ThermalOption.IsChecked = !preferA4;
    }

    public bool IsA4 => A4Option.IsChecked == true;

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
