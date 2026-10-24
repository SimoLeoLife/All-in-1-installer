using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace AllInOneInstaller.Remote;

/// <summary>
/// OpenSSH's bcrypt_pbkdf, which derives the key of an encrypted "openssh-key-v1" private key.
/// Port of OpenBSD's bcrypt_pbkdf.c; the Blowfish initial tables (digits of pi) are computed rather than listed.
/// </summary>
public static class BcryptPbkdf
{
    private const int HashSize = 32;
    private static readonly Lazy<uint[]> PiWords = new(ComputePiWords);

    public static byte[] DeriveKey(byte[] password, byte[] salt, int keyLength, int rounds)
    {
        if (rounds < 1) throw new ArgumentOutOfRangeException(nameof(rounds));
        if (keyLength < 1 || keyLength > HashSize * HashSize) throw new ArgumentOutOfRangeException(nameof(keyLength));

        byte[] key = new byte[keyLength];
        int stride = (keyLength + HashSize - 1) / HashSize;
        int amount = (keyLength + stride - 1) / stride;
        byte[] sha2pass = SHA512.HashData(password);
        byte[] countSalt = [.. salt, 0, 0, 0, 0];
        int remaining = keyLength;

        for (uint count = 1; remaining > 0; count++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(countSalt.AsSpan(salt.Length), count);
            byte[] tmp = Hash(sha2pass, SHA512.HashData(countSalt));
            byte[] output = (byte[])tmp.Clone();
            for (int round = 1; round < rounds; round++)
            {
                tmp = Hash(sha2pass, SHA512.HashData(tmp));
                for (int j = 0; j < output.Length; j++) output[j] ^= tmp[j];
            }

            // Output bytes are interleaved across the key so every block contributes to all of it.
            amount = Math.Min(amount, remaining);
            int i;
            for (i = 0; i < amount; i++)
            {
                int dest = i * stride + (int)(count - 1);
                if (dest >= keyLength) break;
                key[dest] = output[i];
            }
            remaining -= i;
        }
        return key;
    }

    // bcrypt_hash: expensive key schedule, then 64 encryptions of a fixed text.
    private static byte[] Hash(byte[] sha2pass, byte[] sha2salt)
    {
        var blowfish = new Blowfish(PiWords.Value);
        blowfish.ExpandState(sha2salt, sha2pass);
        for (int i = 0; i < 64; i++)
        {
            blowfish.ExpandState(null, sha2salt);
            blowfish.ExpandState(null, sha2pass);
        }

        byte[] text = "OxychromaticBlowfishSwatDynamite"u8.ToArray();
        uint[] data = new uint[8];
        for (int i = 0; i < 8; i++) data[i] = BinaryPrimitives.ReadUInt32BigEndian(text.AsSpan(i * 4));
        for (int i = 0; i < 64; i++)
            for (int j = 0; j < 8; j += 2)
                blowfish.Encipher(ref data[j], ref data[j + 1]);

        byte[] result = new byte[HashSize];
        for (int i = 0; i < 8; i++) BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(i * 4), data[i]);
        return result;
    }

    private sealed class Blowfish
    {
        private readonly uint[] _p = new uint[18];
        private readonly uint[] _s = new uint[4 * 256];

        public Blowfish(uint[] pi)
        {
            Array.Copy(pi, 0, _p, 0, 18);
            Array.Copy(pi, 18, _s, 0, 1024);
        }

        // Blowfish_expandstate; Blowfish_expand0state when data is null.
        public void ExpandState(byte[]? data, byte[] key)
        {
            int keyPos = 0;
            for (int i = 0; i < 18; i++) _p[i] ^= StreamToWord(key, ref keyPos);

            int dataPos = 0;
            uint left = 0, right = 0;
            for (int i = 0; i < 18; i += 2)
            {
                if (data is not null) { left ^= StreamToWord(data, ref dataPos); right ^= StreamToWord(data, ref dataPos); }
                Encipher(ref left, ref right);
                _p[i] = left;
                _p[i + 1] = right;
            }
            for (int i = 0; i < 1024; i += 2)
            {
                if (data is not null) { left ^= StreamToWord(data, ref dataPos); right ^= StreamToWord(data, ref dataPos); }
                Encipher(ref left, ref right);
                _s[i] = left;
                _s[i + 1] = right;
            }
        }

        public void Encipher(ref uint xl, ref uint xr)
        {
            uint l = xl ^ _p[0], r = xr;
            for (int i = 1; i <= 16; i += 2)
            {
                r ^= F(l) ^ _p[i];
                l ^= F(r) ^ _p[i + 1];
            }
            xl = r ^ _p[17];
            xr = l;
        }

        private uint F(uint x) =>
            ((_s[x >> 24] + _s[256 + ((x >> 16) & 0xFF)]) ^ _s[512 + ((x >> 8) & 0xFF)]) + _s[768 + (x & 0xFF)];

        // Big-endian, wrapping at the end of the buffer.
        private static uint StreamToWord(byte[] data, ref int position)
        {
            uint word = 0;
            for (int i = 0; i < 4; i++)
            {
                if (position >= data.Length) position = 0;
                word = (word << 8) | data[position++];
            }
            return word;
        }
    }

    // The first 18 + 1024 32-bit words of pi's fractional part (0x243F6A88, 0x85A308D3, ...).
    private static uint[] ComputePiWords()
    {
        const int words = 18 + 1024;
        const int guardBits = 64;
        int bits = words * 32 + guardBits;
        BigInteger one = BigInteger.One << bits;
        // Machin: pi = 16 atan(1/5) - 4 atan(1/239), in fixed point.
        BigInteger pi = 16 * ArcTanInverse(5, one) - 4 * ArcTanInverse(239, one);
        BigInteger fraction = (pi - 3 * one) >> guardBits;

        uint[] result = new uint[words];
        for (int i = words - 1; i >= 0; i--)
        {
            result[i] = (uint)(fraction & uint.MaxValue);
            fraction >>= 32;
        }
        return result;
    }

    private static BigInteger ArcTanInverse(int x, BigInteger one)
    {
        BigInteger x2 = x * x, term = one / x, sum = term;
        for (int n = 3, sign = -1; !term.IsZero; n += 2, sign = -sign)
        {
            term /= x2;
            sum += sign * (term / n);
        }
        return sum;
    }
}
