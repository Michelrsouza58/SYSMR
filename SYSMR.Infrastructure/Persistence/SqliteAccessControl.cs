using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace SYSMR.Infrastructure.Persistence;

public sealed class SqliteAccessControl(SqliteDatabase database)
{
    private const int Iterations = 210_000;

    public async Task<bool> IsAdministratorConfiguredAsync(CancellationToken cancellationToken = default) =>
        await GetSettingAsync("superadmin.hash", cancellationToken) is not null;

    public async Task ConfigureAdministratorAsync(string pin, CancellationToken cancellationToken = default)
    {
        ValidatePin(pin);
        if (await IsAdministratorConfiguredAsync(cancellationToken))
            throw new InvalidOperationException("O Super Admin já está configurado.");

        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(pin, salt, Iterations, HashAlgorithmName.SHA256, 32);
        await SetSettingAsync("superadmin.salt", Convert.ToBase64String(salt), cancellationToken);
        await SetSettingAsync("superadmin.hash", Convert.ToBase64String(hash), cancellationToken);
    }

    public async Task<bool> VerifyAdministratorPinAsync(string pin, CancellationToken cancellationToken = default)
    {
        var saltText = await GetSettingAsync("superadmin.salt", cancellationToken);
        var hashText = await GetSettingAsync("superadmin.hash", cancellationToken);
        if (saltText is null || hashText is null)
            return false;

        try
        {
            var salt = Convert.FromBase64String(saltText);
            var expected = Convert.FromBase64String(hashText);
            var actual = Rfc2898DeriveBytes.Pbkdf2(pin, salt, Iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("As credenciais locais do Super Admin estão inválidas.");
        }
    }

    public Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default) =>
        ReadSettingAsync(key, cancellationToken);

    public Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default) =>
        WriteSettingAsync(key, value, cancellationToken);

    private async Task<string?> ReadSettingAsync(string key, CancellationToken cancellationToken)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM AppSettings WHERE Key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private async Task WriteSettingAsync(string key, string value, CancellationToken cancellationToken)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AppSettings (Key, Value) VALUES ($key, $value)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void ValidatePin(string pin)
    {
        if (pin.Length is < 8 or > 128)
            throw new ArgumentException("O PIN do Super Admin deve ter entre 8 e 128 caracteres.", nameof(pin));
    }
}
