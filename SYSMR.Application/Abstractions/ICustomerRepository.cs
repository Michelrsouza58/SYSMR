using SYSMR.Domain.Entities;

namespace SYSMR.Application.Abstractions;

public interface ICustomerRepository
{
    Task<IReadOnlyList<Customer>> SearchAsync(string query, CancellationToken cancellationToken = default);
    Task SaveAsync(Customer customer, CancellationToken cancellationToken = default);
    Task RenewAsync(Guid customerId, DateTimeOffset renewedAt, CancellationToken cancellationToken = default);
}
