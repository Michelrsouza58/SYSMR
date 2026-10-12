using System.Windows;

namespace SYSMR.UI;

public partial class LoadingWindow : Window
{
    public LoadingWindow() => InitializeComponent();

    public void SetStatus(string status) => StatusText.Text = status;
}
