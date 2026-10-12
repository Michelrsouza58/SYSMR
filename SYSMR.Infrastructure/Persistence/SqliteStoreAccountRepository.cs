using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace SYSMR.Infrastructure.Persistence;

public sealed record StoreAccountIdentity(
    string CustomerNumber,
    string CustomerName,
    string FirebaseUid,
    string StoreType = "sales",
    string? FirebaseRefreshToken = null);

public sealed class SqliteStoreAccountRepository(SqliteDatabase database)
{
    private const int Iterations = 210_000;

    public async Task SaveAuthenticatedAccountAsync(
        string customerNumber,
        string customerName,
        string password,
        string firebaseUid,
        string storeType = "sales",
        string? firebaseRefreshToken = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedNumber = NormalizeNumber(customerNumber);
        var normalizedName = NormalizeName(customerName);
        if (password.Length is < 8 or > 128)
            throw new ArgumentException("A senha deve ter entre 8 e 128 caracteres.", nameof(password));
        if (string.IsNullOrWhiteSpace(firebaseUid))
            throw new ArgumentException("O identificador Firebase é obrigatório.", nameof(firebaseUid));
        storeType = NormalizeStoreType(storeType);
        var protectedRefreshToken = string.IsNullOrWhiteSpace(firebaseRefreshToken)
            ? null
            : WindowsSecretProtection.Protect(firebaseRefreshToken);

        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);

        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using (var existing = connection.CreateCommand())
        {
            existing.CommandText = "SELECT CustomerNumber, FirebaseUid FROM StoreAccountCache WHERE Id = 1;";
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken) &&
                (!reader.GetString(0).Equals(normalizedNumber, StringComparison.OrdinalIgnoreCase) ||
                 !reader.GetString(1).Equals(firebaseUid, StringComparison.Ordinal)))
                throw new InvalidOperationException(
                    "Este banco local já está vinculado a outra loja. Use um banco local separado para cada loja.");
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO StoreAccountCache
                (Id, CustomerNumber, CustomerName, FirebaseUid, PasswordSalt, PasswordHash, UpdatedAt, AutoLogin, StoreType, ProtectedRefreshToken)
            VALUES
                (1, $number, $name, $uid, $salt, $hash, $updatedAt, 1, $storeType, $refreshToken)
            ON CONFLICT(Id) DO UPDATE SET
                CustomerNumber = excluded.CustomerNumber,
                CustomerName = excluded.CustomerName,
                FirebaseUid = excluded.FirebaseUid,
                PasswordSalt = excluded.PasswordSalt,
                PasswordHash = excluded.PasswordHash,
                UpdatedAt = excluded.UpdatedAt,
                AutoLogin = 1,
                StoreType = excluded.StoreType,
                ProtectedRefreshToken = COALESCE(excluded.ProtectedRefreshToken, StoreAccountCache.ProtectedRefreshToken);
            """;
        command.Parameters.AddWithValue("$number", normalizedNumber);
        command.Parameters.AddWithValue("$name", normalizedName);
        command.Parameters.AddWithValue("$uid", firebaseUid);
        command.Parameters.AddWithValue("$salt", Convert.ToBase64String(salt));
        command.Parameters.AddWithValue("$hash", Convert.ToBase64String(hash));
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$storeType", storeType);
        command.Parameters.AddWithValue("$refreshToken", (object?)protectedRefreshToken ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> HasAccountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM StoreAccountCache WHERE Id = 1);";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    public async Task<StoreAccountIdentity?> GetRememberedAccountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CustomerNumber, CustomerName, FirebaseUid, StoreType
            FROM StoreAccountCache
            WHERE Id = 1 AND AutoLogin = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new StoreAccountIdentity(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3))
            : null;
    }

    public async Task<string?> GetRefreshTokenAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ProtectedRefreshToken FROM StoreAccountCache WHERE Id = 1;";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null or DBNull)
            return null;
        try
        {
            return WindowsSecretProtection.Unprotect((string)value);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("O token de sessão Firebase protegido está inválido.", exception);
        }
    }

    public async Task UpdateStoreTypeAndRefreshTokenAsync(
        string storeType,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        storeType = NormalizeStoreType(storeType);
        var protectedRefreshToken = WindowsSecretProtection.Protect(refreshToken);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE StoreAccountCache
            SET StoreType = $storeType, ProtectedRefreshToken = $refreshToken, UpdatedAt = $updatedAt
            WHERE Id = 1;
            """;
        command.Parameters.AddWithValue("$storeType", storeType);
        command.Parameters.AddWithValue("$refreshToken", protectedRefreshToken);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Não há uma conta de loja local para atualizar.");
    }

    public async Task DisableAutoLoginAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE StoreAccountCache SET AutoLogin = 0 WHERE Id = 1;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task EnableAutoLoginAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE StoreAccountCache SET AutoLogin = 1 WHERE Id = 1;";
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Não há uma conta de loja local para manter conectada.");
    }

    public async Task<StoreAccountIdentity?> VerifyOfflineAsync(
        string customerNumber,
        string customerName,
        string password,
        CancellationToken cancellationToken = default)
    {
        var normalizedNumber = NormalizeNumber(customerNumber);
        var normalizedName = NormalizeName(customerName);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CustomerNumber, CustomerName, FirebaseUid, PasswordSalt, PasswordHash, StoreType
            FROM StoreAccountCache WHERE Id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var savedNumber = reader.GetString(0);
        var savedName = reader.GetString(1);
        var firebaseUid = reader.GetString(2);
        var saltText = reader.GetString(3);
        var hashText = reader.GetString(4);
        var storeType = reader.GetString(5);
        if (!savedNumber.Equals(normalizedNumber, StringComparison.OrdinalIgnoreCase) ||
            !savedName.Equals(normalizedName, StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            var salt = Convert.FromBase64String(saltText);
            var expectedHash = Convert.FromBase64String(hashText);
            var actualHash = Rfc2898DeriveBytes.Pbkdf2(
                password, salt, Iterations, HashAlgorithmName.SHA256, expectedHash.Length);
            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash)
                ? new StoreAccountIdentity(savedNumber, savedName, firebaseUid, storeType)
                : null;
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("O cache local da conta da loja está inválido.");
        }
    }

    public static string NormalizeNumber(string customerNumber) =>
        customerNumber.Trim().ToUpperInvariant();

    private static string NormalizeStoreType(string storeType) =>
        storeType.Trim().ToLowerInvariant() switch
        {
            "restaurant" => "restaurant",
            "sales" => "sales",
            _ => throw new ArgumentException("O tipo de loja deve ser 'sales' ou 'restaurant'.", nameof(storeType))
        };

    private static string NormalizeName(string customerName) => customerName.Trim();
}
