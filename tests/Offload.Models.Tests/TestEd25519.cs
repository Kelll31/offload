using System.Numerics;
using System.Security.Cryptography;

namespace Offload.Models.Tests;

/// <summary>
/// Подпись Ed25519 (RFC 8032, 5.1.6) только для тестов: в программе подписей не создаётся — лишь проверка
/// (Offload.Core.Security.Ed25519). Проверена векторами RFC в <see cref="TestEd25519Tests"/>.
/// </summary>
internal static class TestEd25519
{
    private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
    private static readonly BigInteger L = BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493");
    private static readonly BigInteger D = Mod(-121665 * Inv(121666));
    private static readonly BigInteger I = BigInteger.ModPow(2, (P - 1) / 4, P);
    private static readonly (BigInteger X, BigInteger Y, BigInteger Z, BigInteger T) B = BasePoint();

    public static byte[] PublicKey(byte[] seed) => Encode(Mul(B, SecretScalar(seed)));

    public static byte[] Sign(byte[] seed, byte[] message)
    {
        var h = SHA512.HashData(seed);
        var a = SecretScalar(seed);
        var pk = Encode(Mul(B, a));
        var r = FromLe(SHA512.HashData([.. h[32..], .. message])) % L;
        var rEnc = Encode(Mul(B, r));
        var k = FromLe(SHA512.HashData([.. rEnc, .. pk, .. message])) % L;
        var s = (r + k * a) % L;
        var sig = new byte[64];
        rEnc.CopyTo(sig, 0);
        s.TryWriteBytes(sig.AsSpan(32), out _, isUnsigned: true, isBigEndian: false);
        return sig;
    }

    private static BigInteger SecretScalar(byte[] seed)
    {
        var h = SHA512.HashData(seed)[..32];
        h[0] &= 248;
        h[31] &= 127;
        h[31] |= 64;
        return FromLe(h);
    }

    private static BigInteger Mod(BigInteger a) => ((a % P) + P) % P;
    private static BigInteger Inv(BigInteger a) => BigInteger.ModPow(Mod(a), P - 2, P);
    private static BigInteger FromLe(byte[] b) => new(b, isUnsigned: true, isBigEndian: false);

    private static (BigInteger, BigInteger, BigInteger, BigInteger) Add(
        (BigInteger X, BigInteger Y, BigInteger Z, BigInteger T) p, (BigInteger X, BigInteger Y, BigInteger Z, BigInteger T) q)
    {
        var a = Mod((p.Y - p.X) * (q.Y - q.X));
        var b = Mod((p.Y + p.X) * (q.Y + q.X));
        var c = Mod(p.T * 2 * D * q.T);
        var d = Mod(p.Z * 2 * q.Z);
        var (e, f, g, h) = (b - a, d - c, d + c, b + a);
        return (Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h));
    }

    private static (BigInteger X, BigInteger Y, BigInteger Z, BigInteger T) Mul((BigInteger X, BigInteger Y, BigInteger Z, BigInteger T) p, BigInteger k)
    {
        (BigInteger X, BigInteger Y, BigInteger Z, BigInteger T) q = (0, 1, 1, 0);
        while (k > 0)
        {
            if (!k.IsEven) q = Add(q, p);
            p = Add(p, p);
            k >>= 1;
        }
        return q;
    }

    private static byte[] Encode((BigInteger X, BigInteger Y, BigInteger Z, BigInteger T) p)
    {
        var zi = Inv(p.Z);
        var x = Mod(p.X * zi);
        var y = Mod(p.Y * zi);
        var bytes = new byte[32];
        y.TryWriteBytes(bytes, out _, isUnsigned: true, isBigEndian: false);
        if (!x.IsEven) bytes[31] |= 0x80;
        return bytes;
    }

    private static (BigInteger, BigInteger, BigInteger, BigInteger) BasePoint()
    {
        var y = Mod(4 * Inv(5));
        var u = Mod(y * y - 1);
        var v = Mod(D * y * y + 1);
        var x = BigInteger.ModPow(Mod(u * Inv(v)), (P + 3) / 8, P);
        if (Mod(v * x * x) != u) x = Mod(x * I);
        if (!x.IsEven) x = P - x;
        return (x, y, 1, Mod(x * y));
    }
}

public sealed class TestEd25519Tests
{
    [Theory]
    [InlineData("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60", "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a", "",
        "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b")]
    [InlineData("4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb", "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c", "72",
        "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00")]
    [InlineData("c5aa8df43f9f837bedb7442f31dcb7b166d38535076f094b85ce3a2e0b4458f7", "fc51cd8e6218a1a38da47ed00230f0580816ed13ba3303ac5deb911548908025", "af82",
        "6291d657deec24024827e69c3abe01a30ce548a284743a445e3680d7db5ac3ac18ff9b538d16f290ae67f760984dc6594a7c15e9716ed28dc027beceea1ec40a")]
    public void Signer_MatchesRfc8032(string seed, string publicKey, string message, string signature)
    {
        var s = Convert.FromHexString(seed);
        Assert.Equal(publicKey, Convert.ToHexString(TestEd25519.PublicKey(s)).ToLowerInvariant());
        var sig = TestEd25519.Sign(s, Convert.FromHexString(message));
        Assert.Equal(signature, Convert.ToHexString(sig).ToLowerInvariant());
        Assert.True(Offload.Core.Security.Ed25519.Verify(Convert.FromHexString(publicKey), Convert.FromHexString(message), sig));
    }
}
