using System.Collections.Generic;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace KnotGarden.Infrastructure.Persistence;

/// <summary>
/// EF-backed <see cref="INotificationChannelStore"/> over the shared <see cref="AppDbContext"/>. Owns
/// reads of the <c>NotificationChannels</c> table so the Send Notification node and the failure-alert
/// channel resolver never bind the concrete DbContext.
/// </summary>
public sealed class DbNotificationChannelStore : INotificationChannelStore
{
    private readonly AppDbContext _dbContext;

    public DbNotificationChannelStore(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<NotificationChannel?> GetAsync(string channelId, CancellationToken cancellationToken = default)
        => await _dbContext.NotificationChannels.FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken);

    public async Task<IReadOnlyList<NotificationChannel>> ListAsync(CancellationToken cancellationToken = default)
        => await _dbContext.NotificationChannels.ToListAsync(cancellationToken);
}
