using System.Security.Cryptography;
using System.Text;

namespace ShareKM;

// The shared passphrase: generating, normalising, storing (DPAPI) and turning it into the 32-byte master key.
static class Pairing
{
    public const int MinLength = 8, Iterations = 600_000;
    const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // 32 symbols: no 0/O, 1/I
    static readonly byte[] Salt = "ShareKM pairing v1"u8.ToArray();
    static readonly byte[] Entropy = "ShareKM passphrase v1"u8.ToArray();

    // 16 symbols x 5 bits = 80 bits, typed as XXXX-XXXX-XXXX-XXXX.
    public static string Generate()
    {
        var sb = new StringBuilder(19);
        for (int i = 0; i < 16; i++)
        {
            if (i > 0 && i % 4 == 0) sb.Append('-');
            sb.Append(Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]);
        }
        return sb.ToString();
    }

    // Trim and NFC; a generated-style code is accepted in any case and with spaces or dashes.
    public static string Normalize(string? passphrase)
    {
        var p = (passphrase ?? "").Trim().Normalize(NormalizationForm.FormC);
        var compact = new string(p.Where(ch => ch is not (' ' or '-')).Select(char.ToUpperInvariant).ToArray());
        if (compact.Length == 16 && compact.All(Alphabet.Contains) && p.Length <= 19)
            return string.Join("-", Enumerable.Range(0, 4).Select(i => compact.Substring(i * 4, 4)));
        return p;
    }

    // Slow on purpose (about half a second): run it off the UI thread.
    public static byte[] DeriveMaster(string normalized) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(normalized), Salt, Iterations, HashAlgorithmName.SHA256, 32);

    // DPAPI, current user: the stored value only decrypts for this Windows user on this PC.
    public static string Protect(string passphrase) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(passphrase), Entropy, DataProtectionScope.CurrentUser));

    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), Entropy, DataProtectionScope.CurrentUser)); }
        catch (Exception e) when (e is CryptographicException or FormatException) { return null; }
    }
}

// Key schedule and frame sealing for the link. See Link.cs for the handshake.
static class LinkCrypto
{
    public const int TagSize = 16, CounterSize = 8;
    static readonly byte[] InfoDtoL = "ShareKM v2 key dialer->listener"u8.ToArray(),
        InfoLtoD = "ShareKM v2 key listener->dialer"u8.ToArray(), InfoAuth = "ShareKM v2 auth"u8.ToArray();
    public static readonly byte[] LabelDialer = "ShareKM v2 dialer"u8.ToArray(), LabelListener = "ShareKM v2 listener"u8.ToArray();

    public static byte[] Hkdf(byte[] ikm, byte[] salt, byte[] info, int length)
    {
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        return HKDF.Expand(HashAlgorithmName.SHA256, prk, length, info);
    }

    // salt = dialer nonce || listener nonce
    public static (byte[] dl, byte[] ld, byte[] auth) SessionKeys(byte[] master, byte[] salt) =>
        (Hkdf(master, salt, InfoDtoL, 32), Hkdf(master, salt, InfoLtoD, 32), Hkdf(master, salt, InfoAuth, 32));

    public static byte[] AuthTag(byte[] authKey, byte[] label, byte[] transcript) =>
        HMACSHA256.HashData(authKey, (byte[])[.. label, .. transcript]);

    static void Nonce(Span<byte> nonce, ulong counter)
    {
        nonce[..4].Clear();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], counter);
    }

    // Wire frame: int32 length | uint64 counter (big-endian) | ciphertext | 16-byte tag.
    public static byte[] Seal(AesGcm aes, ulong counter, ReadOnlySpan<byte> plain)
    {
        var frame = new byte[4 + CounterSize + plain.Length + TagSize];
        BitConverter.TryWriteBytes(frame.AsSpan(0, 4), frame.Length - 4);
        var ctr = frame.AsSpan(4, CounterSize);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(ctr, counter);
        Span<byte> nonce = stackalloc byte[12];
        Nonce(nonce, counter);
        aes.Encrypt(nonce, plain, frame.AsSpan(4 + CounterSize, plain.Length), frame.AsSpan(frame.Length - TagSize), ctr);
        return frame;
    }

    // body = everything after the length prefix. Throws CryptographicException if the counter is not the
    // expected one (replayed, reordered or dropped frame) or the tag does not verify (tampered).
    public static byte[] Open(AesGcm aes, ulong expected, ReadOnlySpan<byte> body)
    {
        if (body.Length < CounterSize + TagSize) throw new CryptographicException("short frame");
        var ctr = body[..CounterSize];
        if (System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(ctr) != expected) throw new CryptographicException("unexpected counter");
        var plain = new byte[body.Length - CounterSize - TagSize];
        Span<byte> nonce = stackalloc byte[12];
        Nonce(nonce, expected);
        aes.Decrypt(nonce, body.Slice(CounterSize, plain.Length), body[^TagSize..], plain, ctr);
        return plain;
    }
}
