using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Structura.Web.Domain;

namespace Structura.Web.Infrastructure.Auth;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    public string Issuer { get; set; } = "structura";
    public string Audience { get; set; } = "structura";
    public string SigningKey { get; set; } = "";
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 14;
}

public static class AppClaims
{
    public const string Role = "role";
    public const string SecurityStamp = "sst";
    public const string MustChangePassword = "pwd";
}

public sealed class JwtTokenService(Microsoft.Extensions.Options.IOptions<JwtOptions> options)
{
    private readonly JwtOptions _options = options.Value;

    public (string Token, int ExpiresInSeconds) CreateAccessToken(User user)
    {
        var now = DateTimeOffset.UtcNow;
        var expires = now.AddMinutes(_options.AccessTokenMinutes);
        // Raw JWT claim names throughout (no inbound/outbound claim-type mapping anywhere).
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(JwtRegisteredClaimNames.Name, user.FullName),
            new(AppClaims.Role, user.Role),
            new(AppClaims.SecurityStamp, user.SecurityStamp),
            new(AppClaims.MustChangePassword, user.MustChangePassword ? "true" : "false"),
        };
        var creds = new SigningCredentials(CreateKey(_options.SigningKey), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expires.UtcDateTime,
            signingCredentials: creds);
        var handler = new JwtSecurityTokenHandler();
        handler.OutboundClaimTypeMap.Clear();
        return (handler.WriteToken(token), (int)(expires - now).TotalSeconds);
    }

    public static SymmetricSecurityKey CreateKey(string signingKey)
    {
        if (string.IsNullOrWhiteSpace(signingKey) || Encoding.UTF8.GetByteCount(signingKey) < 32)
            throw new InvalidOperationException(
                "JWT signing key is missing or shorter than 32 bytes. Set JWT_SIGNING_KEY.");
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));
    }

    public static string GenerateRefreshToken() =>
        Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(64));

    public static string HashRefreshToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
