using System.Security.Cryptography;
using System.Text;
using Iridium.Server.Configuration;
using Iridium.Server.Domain;
using Iridium.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Iridium.Server.Security;

public sealed class SessionService(IOptions<NodeOptions> options)
{
    public async Task<(string Token, AccountSession Session)> CreateAsync(NodeAccount account, IridiumDbContext db)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var now = DateTimeOffset.UtcNow;
        var session = new AccountSession
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            Account = account,
            TokenHash = HashToken(token),
            CreatedAt = now,
            LastUsedAt = now,
            ExpiresAt = now.AddDays(Math.Max(1, options.Value.SessionAbsoluteDays))
        };
        db.AccountSessions.Add(session);
        await db.SaveChangesAsync();
        return (token, session);
    }

    public async Task<AccountSession?> GetAsync(HttpContext context, IridiumDbContext db)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var token = authorization[7..].Trim();
        if (token.Length == 0) return null;

        return await GetByTokenAsync(token, db);
    }

    public async Task<AccountSession?> GetByTokenAsync(string token, IridiumDbContext db)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var session = await db.AccountSessions.Include(value => value.Account)
            .SingleOrDefaultAsync(value => value.TokenHash == HashToken(token));
        if (session is null) return null;

        var now = DateTimeOffset.UtcNow;
        var idleLifetime = TimeSpan.FromDays(Math.Max(1, options.Value.SessionIdleDays));
        if (session.ExpiresAt <= now || session.LastUsedAt + idleLifetime <= now)
        {
            db.AccountSessions.Remove(session);
            await db.SaveChangesAsync();
            return null;
        }

        var updateInterval = TimeSpan.FromMinutes(Math.Max(1, options.Value.SessionActivityUpdateMinutes));
        if (session.LastUsedAt + updateInterval <= now)
        {
            session.LastUsedAt = now;
            await db.SaveChangesAsync();
        }

        return session;
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
