namespace SYSMR.Domain.Entities;

public sealed record AuditEntry(
    Guid Id,
    DateTimeOffset CreatedAt,
    string Actor,
    string Action,
    string Details);
