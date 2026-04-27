using Audit.Core;
using Brusca.Core.Contracts.Logging;
using Brusca.Core.Models.Logging;
using System.Text.Json;

namespace Brusca.Infrastructure.Logging;

/// <summary>
/// Audit logger backed by Audit.NET.
/// Writes to audit.Log table via Audit.NET SqlServer data provider,
/// and optionally to file or Elasticsearch — configured in appsettings.json.
/// </summary>
public sealed class AuditNetAuditLogger : IAuditLogger
{
    public async Task LogAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
        await using var scope = await AuditScope.CreateAsync(entry.EventType, () => entry);
    }

    public async Task LogAsync(
        string eventType, string entityType, string? entityId = null,
        string? userId = null, string? action = null,
        object? oldValues = null, object? newValues = null,
        CancellationToken ct = default)
    {
        var entry = new AuditLogEntry
        {
            EventType = eventType,
            EntityType = entityType,
            EntityId = entityId,
            UserId = userId,
            Action = action,
            OldValues = oldValues is not null ? JsonSerializer.Serialize(oldValues) : null,
            NewValues = newValues is not null ? JsonSerializer.Serialize(newValues) : null
        };
        await LogAsync(entry, ct);
    }
}
