namespace SYSMR.Domain.Entities;

public enum CashMovementType
{
    OpeningSupply,
    Supply,
    Withdrawal,
    Sale,
    Refund
}

public sealed record CashMovement(Guid Id, Guid SessionId, DateTimeOffset CreatedAt, CashMovementType Type, decimal Amount, string Description);

public sealed class CashSession
{
    public CashSession(Guid id, DateTimeOffset openedAt, decimal openingAmount, string operatorName)
    {
        if (openingAmount < 0)
            throw new ArgumentOutOfRangeException(nameof(openingAmount));
        Id = id;
        OpenedAt = openedAt;
        OpeningAmount = openingAmount;
        OperatorName = operatorName;
    }

    public Guid Id { get; }
    public DateTimeOffset OpenedAt { get; }
    public DateTimeOffset? ClosedAt { get; private set; }
    public decimal OpeningAmount { get; }
    public string OperatorName { get; }
    public bool IsOpen => ClosedAt is null;

    public void Close(DateTimeOffset closedAt)
    {
        if (!IsOpen)
            throw new InvalidOperationException("O caixa ja esta fechado.");
        if (closedAt < OpenedAt)
            throw new ArgumentOutOfRangeException(nameof(closedAt));
        ClosedAt = closedAt;
    }
}

public enum UserRole
{
    Administrator,
    Operator
}

public enum SynchronizationStatus
{
    Pending,
    Synchronized
}
