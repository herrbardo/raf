using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Raf.Contracts;
using Raf.Server.Data;

namespace Raf.Server.Auth;

public static class RafClaims
{
    public const string Subject = "sub";
    public const string Email = "email";
    public const string Name = "name";
    public const string Admin = "raf_admin";

    /// <summary>Presente mientras el usuario tenga contraseña temporal: solo puede cambiarla.</summary>
    public const string PasswordChange = "raf_pwd_change";

    public static Guid GetUserId(this ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue(Subject) ?? throw new InvalidOperationException("Usuario sin 'sub'."));
}

public static class Policies
{
    /// <summary>Autenticado, aunque tenga contraseña temporal pendiente de cambio.</summary>
    public const string AllowPasswordChange = "AllowPasswordChange";

    public const string Admin = "Admin";
}

public sealed class JwtOptions
{
    public const string Section = "Jwt";

    [Required] public string Issuer { get; set; } = "raf";
    [Required] public string Audience { get; set; } = "raf-client";

    /// <summary>Clave HMAC-SHA256; mínimo 32 caracteres. En AWS viene de Secrets Manager.</summary>
    [Required, MinLength(32)] public string SigningKey { get; set; } = "";

    [Range(1, 1440)] public int AccessTokenMinutes { get; set; } = 15;
    [Range(1, 365)] public int RefreshTokenDays { get; set; } = 30;

    public SymmetricSecurityKey GetKey() => new(Encoding.UTF8.GetBytes(SigningKey));
}

public sealed class TokenService(RafDbContext db, IOptions<JwtOptions> options, TimeProvider clock)
{
    private readonly JwtOptions _opt = options.Value;

    public async Task<TokenResponse> IssueAsync(AppUser user, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var (refresh, _) = await CreateRefreshTokenAsync(user.Id, now, ct);
        await db.SaveChangesAsync(ct);
        return Build(user, now, refresh);
    }

    /// <summary>
    /// Rota el refresh token. Si llega uno ya revocado se asume robo y se revocan todas las sesiones del usuario.
    /// Devuelve null si el token no es válido.
    /// </summary>
    public async Task<TokenResponse?> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == Hash(refreshToken), ct);
        if (stored is null) return null;

        if (stored.RevokedAt is not null)
        {
            await RevokeAllAsync(stored.UserId, ct);
            return null;
        }

        var user = await db.Users.FindAsync([stored.UserId], ct);
        if (stored.ExpiresAt <= now || user is null || user.IsDisabled) return null;

        var (newToken, newEntity) = await CreateRefreshTokenAsync(user.Id, now, ct);
        stored.RevokedAt = now;
        stored.ReplacedByTokenId = newEntity.Id;
        await db.SaveChangesAsync(ct);
        return Build(user, now, newToken);
    }

    public async Task RevokeAsync(string refreshToken, CancellationToken ct)
    {
        var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == Hash(refreshToken), ct);
        if (stored is { RevokedAt: null })
        {
            stored.RevokedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
    }

    public Task RevokeAllAsync(Guid userId, CancellationToken ct) =>
        db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, clock.GetUtcNow()), ct);

    private async Task<(string Token, RefreshToken Entity)> CreateRefreshTokenAsync(
        Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var token = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var entity = new RefreshToken
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            TokenHash = Hash(token),
            CreatedAt = now,
            ExpiresAt = now.AddDays(_opt.RefreshTokenDays),
        };
        await db.RefreshTokens.AddAsync(entity, ct);
        return (token, entity);
    }

    private TokenResponse Build(AppUser user, DateTimeOffset now, string refreshToken)
    {
        var expires = now.AddMinutes(_opt.AccessTokenMinutes);
        var claims = new List<Claim>
        {
            new(RafClaims.Subject, user.Id.ToString()),
            new(RafClaims.Email, user.Email!),
            new(RafClaims.Name, user.DisplayName),
        };
        if (user.IsAdmin) claims.Add(new(RafClaims.Admin, "true"));
        if (user.MustChangePassword) claims.Add(new(RafClaims.PasswordChange, "true"));

        var access = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Issuer = _opt.Issuer,
            Audience = _opt.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(_opt.GetKey(), SecurityAlgorithms.HmacSha256),
        });

        return new TokenResponse(access, expires, refreshToken, now.AddDays(_opt.RefreshTokenDays), user.ToDto());
    }

    private static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

public static class PasswordGenerator
{
    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Lower = "abcdefghijkmnpqrstuvwxyz";
    private const string Digits = "23456789";

    /// <summary>Contraseña temporal legible (sin 0/O/1/l/I) que cumple la política de Identity.</summary>
    public static string Generate(int length = 14)
    {
        const string all = Upper + Lower + Digits;
        var chars = new char[length];
        chars[0] = Upper[RandomNumberGenerator.GetInt32(Upper.Length)];
        chars[1] = Lower[RandomNumberGenerator.GetInt32(Lower.Length)];
        chars[2] = Digits[RandomNumberGenerator.GetInt32(Digits.Length)];
        for (var i = 3; i < length; i++) chars[i] = all[RandomNumberGenerator.GetInt32(all.Length)];
        RandomNumberGenerator.Shuffle(chars.AsSpan());
        return new string(chars);
    }
}

public static class UserMapping
{
    public static UserDto ToDto(this AppUser u) =>
        new(u.Id, u.Email!, u.DisplayName, u.IsAdmin, u.IsDisabled, u.MustChangePassword);

    public static UserSummaryDto ToSummary(this AppUser u) => new(u.Id, u.Email!, u.DisplayName);
}
