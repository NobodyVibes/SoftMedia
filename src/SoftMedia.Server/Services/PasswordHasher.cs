using Konscious.Security.Cryptography;
using System.Security.Cryptography;
using System.Text;

namespace SoftMedia.Server.Services;

public interface IPasswordHasher
{
    string HashPassword(string password);
    bool VerifyPassword(string password, string hash);
}

public class PasswordHasher : IPasswordHasher
{
    // Argon2id Configuration
    private const int DegreeOfParallelism = 8;
    private const int MemorySize = 65536; // 64 MB
    private const int Iterations = 4;
    private const int SaltSize = 16;
    private const int KeySize = 32;

    public string HashPassword(string password)
    {
        var salt = new byte[SaltSize];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(salt);
        }

        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password));
        argon2.Salt = salt;
        argon2.DegreeOfParallelism = DegreeOfParallelism;
        argon2.MemorySize = MemorySize;
        argon2.Iterations = Iterations;

        var hash = argon2.GetBytes(KeySize);

        // Format: $argon2id$v=19$m=65536,t=4,p=8$saltBase64$hashBase64
        return $"$argon2id$v=19$m={MemorySize},t={Iterations},p={DegreeOfParallelism}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool VerifyPassword(string password, string hashString)
    {
        try
        {
            // Parse the hash string
            var parts = hashString.Split('$');
            if (parts.Length != 6) return false;

            // parts[0] is empty, parts[1] is "argon2id", parts[2] is "v=19"
            // parts[3] is params "m=...,t=...,p=..."
            // parts[4] is salt
            // parts[5] is hash

            var salt = Convert.FromBase64String(parts[4]);
            var originalHash = Convert.FromBase64String(parts[5]);

            using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password));
            argon2.Salt = salt;
            argon2.DegreeOfParallelism = DegreeOfParallelism;
            argon2.MemorySize = MemorySize;
            argon2.Iterations = Iterations;

            var newHash = argon2.GetBytes(KeySize);

            return CryptographicOperations.FixedTimeEquals(originalHash, newHash);
        }
        catch
        {
            return false;
        }
    }
}
