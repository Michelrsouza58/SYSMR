using System.Windows;
using SYSMR.Domain.Entities;

namespace SYSMR.UI;

public partial class CustomerEditorWindow : Window
{
    private readonly Customer? _customer;

    public CustomerEditorWindow(Customer? customer)
    {
        InitializeComponent();
        _customer = customer;
        if (customer is null)
            return;
        TitleText.Text = "Editar cliente";
        NameBox.Text = customer.Name;
        DocumentBox.Text = customer.Document;
        PhoneBox.Text = customer.Phone;
        EmailBox.Text = customer.Email;
    }

    public Customer? Result { get; private set; }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            Result = new Customer(
                _customer?.Id ?? Guid.NewGuid(),
                NameBox.Text,
                DocumentBox.Text,
                PhoneBox.Text,
                EmailBox.Text,
                _customer?.RegisteredAt ?? now,
                _customer?.ExpiresAt ?? now.AddDays(30));
            DialogResult = true;
        }
        catch (Exception exception)
        {
            ErrorText.Text = exception.Message;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
