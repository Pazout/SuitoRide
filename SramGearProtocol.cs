using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace SuitoRide;

internal static class SramGearProtocol
{
    static readonly BigInteger Modulus = (BigInteger.One << 128) - 713;
    internal static readonly byte[] Init = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();

    internal static byte[] PublicKey(byte[] secret) =>
        ToWire(BigInteger.ModPow(5, new BigInteger(secret, isUnsigned: true), Modulus));

    internal static byte[] ReadBondKey(byte[] secret, byte[] remote, byte[] envelope)
    {
        var peer = new BigInteger(remote, isUnsigned: true, isBigEndian: true);
        if (remote.Length != 16 || envelope.Length != 48 || peer <= 1 || peer >= Modulus - 1)
            throw new InvalidDataException();

        byte[] shared = ToWire(BigInteger.ModPow(peer, new BigInteger(secret, isUnsigned: true), Modulus));
        try { return Decrypt(shared, envelope); }
        finally { CryptographicOperations.ZeroMemory(shared); }
    }

    internal static int? ReadAuthenticatedRearGear(byte[] key, byte[] frame)
    {
        byte[] payload = Decrypt(key, frame);
        try { return ReadRearGear(payload); }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }

    internal static int? ReadRearGear(ReadOnlySpan<byte> input)
    {
        int? gear = null;
        while (!input.IsEmpty)
        {
            ulong tag = TakeVarint(ref input);
            if (tag >> 3 == 0) throw new InvalidDataException();
            if ((tag & 7) == 0)
            {
                ulong value = TakeVarint(ref input);
                if (tag >> 3 == 21) gear = value is >= 1 and <= 13 ? (int)value : null;
                continue;
            }
            ulong length = (tag & 7) switch
            {
                1 => 8,
                2 => TakeVarint(ref input),
                5 => 4,
                _ => throw new InvalidDataException()
            };
            if (length > (ulong)input.Length) throw new InvalidDataException();
            input = input[(int)length..];
        }
        return gear;
    }

    static ulong TakeVarint(ref ReadOnlySpan<byte> input)
    {
        ulong value = 0;
        for (int shift = 0; shift < 64 && !input.IsEmpty; shift += 7)
        {
            byte next = input[0];
            input = input[1..];
            if (shift == 63 && next > 1) break;
            value |= (ulong)(next & 0x7f) << shift;
            if ((next & 0x80) == 0) return value;
        }
        throw new InvalidDataException();
    }

    static byte[] Decrypt(byte[] key, byte[] frame)
    {
        if (key.Length != 16 || frame.Length < 32 || frame.Length > 512)
            throw new InvalidDataException();
        var cipher = new EaxBlockCipher(new AesEngine());
        cipher.Init(false, new AeadParameters(new KeyParameter(key), 128, frame[..16]));
        byte[] plaintext = new byte[frame.Length - 32];
        try
        {
            int count = cipher.ProcessBytes(frame, 16, frame.Length - 16, plaintext, 0);
            if (count + cipher.DoFinal(plaintext, count) != plaintext.Length) throw new InvalidDataException();
            return plaintext;
        }
        catch (InvalidCipherTextException error)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new CryptographicException("SRAMBond authentication failed.", error);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
    }

    static byte[] ToWire(BigInteger value)
    {
        byte[] bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (bytes.Length == 16) return bytes;
        byte[] padded = new byte[16];
        bytes.CopyTo(padded, 16 - bytes.Length);
        CryptographicOperations.ZeroMemory(bytes);
        return padded;
    }
}
