namespace SYSMR.Application.Models;

public sealed record SellerRecord(Guid Id, string Name, bool IsActive)
{
    public string StatusLabel => IsActive ? "Ativo" : "Inativo";
}