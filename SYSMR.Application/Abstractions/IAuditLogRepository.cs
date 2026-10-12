using SYSMR.Domain.Entities;

namespace SYSMR.Application.Abstractions;

public interface IAuditLogRepository
{
    Task RecordAsync(string actor, string action, string details, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AuditEntry>> SearchAsync(string query, CancellationToken cancellationToken = default);
}
