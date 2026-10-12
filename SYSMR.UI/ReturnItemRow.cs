using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SYSMR.UI;

public sealed class ReturnItemRow : INotifyPropertyChanged
{
    private decimal _quantityToReturn;

    public ReturnItemRow(Guid productId, string productName, decimal soldQuantity, decimal returnedQuantity, decimal unitPrice)
    {
        ProductId = productId;
        ProductName = productName;
        SoldQuantity = soldQuantity;
        ReturnedQuantity = returnedQuantity;
        UnitPrice = unitPrice;
    }

    public Guid ProductId { get; }
    public string ProductName { get; }
    public decimal SoldQuantity { get; }
    public decimal ReturnedQuantity { get; }
    public decimal AvailableQuantity => SoldQuantity - ReturnedQuantity;
    public decimal UnitPrice { get; }

    public decimal QuantityToReturn
    {
        get => _quantityToReturn;
        set
        {
            if (_quantityToReturn == value)
                return;
            _quantityToReturn = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RefundAmount));
        }
    }

    public decimal RefundAmount =>
        decimal.Round(QuantityToReturn * UnitPrice, 2, MidpointRounding.AwayFromZero);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
