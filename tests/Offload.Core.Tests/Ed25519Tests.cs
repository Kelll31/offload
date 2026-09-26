using System.Numerics;
using System.Text;
using Offload.Core.Security;

namespace Offload.Core.Tests;

/// <summary>Проверка подписи Ed25519: векторы RFC 8032 (7.1), совместимость с OpenSSL и отказы на испорченных данных.</summary>
public sealed class Ed25519Tests
{
    public sealed record Vector(string Name, string PublicKey, string Message, string Signature);

    // RFC 8032, раздел 7.1: TEST 1, TEST 2, TEST 3, TEST 1024 (сообщение 1023 байта).
    public static readonly Vector Test1 = new("TEST 1",
        "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a",
        "",
        "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b");

    public static readonly Vector Test2 = new("TEST 2",
        "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c",
        "72",
        "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00");

    public static readonly Vector Test3 = new("TEST 3",
        "fc51cd8e6218a1a38da47ed00230f0580816ed13ba3303ac5deb911548908025",
        "af82",
        "6291d657deec24024827e69c3abe01a30ce548a284743a445e3680d7db5ac3ac18ff9b538d16f290ae67f760984dc6594a7c15e9716ed28dc027beceea1ec40a");

    public static readonly Vector Test1024 = new("TEST 1024",
        "278117fc144c72340f67d0f2316e8386ceffbf2b2428c9c51fef7c597f1d426e",
        "08b8b2b733424243760fe426a4b54908632110a66c2f6591eabd3345e3e4eb98fa6e264bf09efe12ee50f8f54e9f77b1e355f6c50544e23fb1433ddf73be84d8" +
        "79de7c0046dc4996d9e773f4bc9efe5738829adb26c81b37c93a1b270b20329d658675fc6ea534e0810a4432826bf58c941efb65d57a338bbd2e26640f89ffbc" +
        "1a858efcb8550ee3a5e1998bd177e93a7363c344fe6b199ee5d02e82d522c4feba15452f80288a821a579116ec6dad2b3b310da903401aa62100ab5d1a36553e" +
        "06203b33890cc9b832f79ef80560ccb9a39ce767967ed628c6ad573cb116dbefefd75499da96bd68a8a97b928a8bbc103b6621fcde2beca1231d206be6cd9ec7" +
        "aff6f6c94fcd7204ed3455c68c83f4a41da4af2b74ef5c53f1d8ac70bdcb7ed185ce81bd84359d44254d95629e9855a94a7c1958d1f8ada5d0532ed8a5aa3fb2" +
        "d17ba70eb6248e594e1a2297acbbb39d502f1a8c6eb6f1ce22b3de1a1f40cc24554119a831a9aad6079cad88425de6bde1a9187ebb6092cf67bf2b13fd65f270" +
        "88d78b7e883c8759d2c4f5c65adb7553878ad575f9fad878e80a0c9ba63bcbcc2732e69485bbc9c90bfbd62481d9089beccf80cfe2df16a2cf65bd92dd597b07" +
        "07e0917af48bbb75fed413d238f5555a7a569d80c3414a8d0859dc65a46128bab27af87a71314f318c782b23ebfe808b82b0ce26401d2e22f04d83d1255dc51a" +
        "ddd3b75a2b1ae0784504df543af8969be3ea7082ff7fc9888c144da2af58429ec96031dbcad3dad9af0dcbaaaf268cb8fcffead94f3c7ca495e056a9b47acdb7" +
        "51fb73e666c6c655ade8297297d07ad1ba5e43f1bca32301651339e22904cc8c42f58c30c04aafdb038dda0847dd988dcda6f3bfd15c4b4c4525004aa06eeff8" +
        "ca61783aacec57fb3d1f92b0fe2fd1a85f6724517b65e614ad6808d6f6ee34dff7310fdc82aebfd904b01e1dc54b2927094b2db68d6f903b68401adebf5a7e08" +
        "d78ff4ef5d63653a65040cf9bfd4aca7984a74d37145986780fc0b16ac451649de6188a7dbdf191f64b5fc5e2ab47b57f7f7276cd419c17a3ca8e1b939ae49e4" +
        "88acba6b965610b5480109c8b17b80e1b7b750dfc7598d5d5011fd2dcc5600a32ef5b52a1ecc820e308aa342721aac0943bf6686b64b2579376504ccc493d97e" +
        "6aed3fb0f9cd71a43dd497f01f17c0e2cb3797aa2a2f256656168e6c496afc5fb93246f6b1116398a346f1a641f3b041e989f7914f90cc2c7fff357876e506b5" +
        "0d334ba77c225bc307ba537152f3f1610e4eafe595f6d9d90d11faa933a15ef1369546868a7f3a45a96768d40fd9d03412c091c6315cf4fde7cb68606937380d" +
        "b2eaaa707b4c4185c32eddcdd306705e4dc1ffc872eeee475a64dfac86aba41c0618983f8741c5ef68d3a101e8a3b8cac60c905c15fc910840b94c00a0b9d0",
        "0aab4c900501b3e24d7cdf4663326a3a87df5e4843b2cbdb67cbf6e460fec350aa5371b1508f9f4528ecea23c436d94b5e8fcd4f681e30a6ac00a9704a188a03");

    public static TheoryData<string> Names => new(Test1.Name, Test2.Name, Test3.Name, Test1024.Name);

    private static Vector ByName(string name) => new[] { Test1, Test2, Test3, Test1024 }.Single(v => v.Name == name);

    private static byte[] Hex(string s) => Convert.FromHexString(s);

    [Theory]
    [MemberData(nameof(Names))]
    public void Verify_Rfc8032Vectors_Accepted(string name)
    {
        var v = ByName(name);
        Assert.True(Ed25519.Verify(Hex(v.PublicKey), Hex(v.Message), Hex(v.Signature)), $"вектор {name} должен проходить");
    }

    [Fact]
    public void Verify_Test1024_MessageIs1023Bytes()
    {
        Assert.Equal(1023, Hex(Test1024.Message).Length);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Verify_FlippedSignatureBit_Rejected(string name)
    {
        var v = ByName(name);
        foreach (var bit in new[] { 0, 7, 100, 255, 256, 300, 511 })
        {
            var sig = Hex(v.Signature);
            sig[bit / 8] ^= (byte)(1 << (bit % 8));
            Assert.False(Ed25519.Verify(Hex(v.PublicKey), Hex(v.Message), sig), $"{name}: подпись с изменённым битом {bit} принята");
        }
    }

    [Fact]
    public void Verify_ChangedMessage_Rejected()
    {
        Assert.False(Ed25519.Verify(Hex(Test2.PublicKey), Hex("73"), Hex(Test2.Signature)));
        Assert.False(Ed25519.Verify(Hex(Test3.PublicKey), Hex("af82ff"), Hex(Test3.Signature)));
        var msg = Hex(Test1024.Message);
        msg[500] ^= 0x01;
        Assert.False(Ed25519.Verify(Hex(Test1024.PublicKey), msg, Hex(Test1024.Signature)));
    }

    [Fact]
    public void Verify_WrongKey_Rejected()
    {
        Assert.False(Ed25519.Verify(Hex(Test2.PublicKey), Hex(Test1.Message), Hex(Test1.Signature)));
        Assert.False(Ed25519.Verify(Hex(Test1.PublicKey), Hex(Test2.Message), Hex(Test2.Signature)));
    }

    [Fact]
    public void Verify_NonCanonicalS_Rejected()
    {
        // S + L даёт ту же точку [S]B, но RFC 8032 требует S < L — иначе подпись «пластична».
        var l = BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493");
        var sig = Hex(Test1.Signature);
        var s = new BigInteger(sig.AsSpan(32), isUnsigned: true, isBigEndian: false);
        var bumped = new byte[32];
        Assert.True((s + l).TryWriteBytes(bumped, out _, isUnsigned: true, isBigEndian: false));
        bumped.CopyTo(sig, 32);
        Assert.False(Ed25519.Verify(Hex(Test1.PublicKey), Hex(Test1.Message), sig), "подпись с S ≥ L принята");
    }

    [Fact]
    public void Verify_InvalidInputs_Rejected()
    {
        var pk = Hex(Test1.PublicKey);
        var sig = Hex(Test1.Signature);
        Assert.False(Ed25519.Verify(pk.AsSpan(0, 31), [], sig));
        Assert.False(Ed25519.Verify(pk, [], sig.AsSpan(0, 63)));
        Assert.False(Ed25519.Verify([], [], []));
        // Неканоническая запись ключа: y = 2^255 − 1 ≥ p.
        var nonCanonical = Enumerable.Repeat((byte)0xFF, 32).ToArray();
        nonCanonical[31] = 0x7F;
        Assert.False(Ed25519.Verify(nonCanonical, [], sig));
        // Нулевая подпись и нулевой ключ.
        Assert.False(Ed25519.Verify(new byte[32], [], new byte[64]));
        Assert.False(Ed25519.Verify(pk, [], new byte[64]));
    }

    /// <summary>Запись точки: y (little-endian, 255 бит) и знак x в старшем бите.</summary>
    private static byte[] EncodeY(BigInteger y, bool xSign = false)
    {
        var bytes = new byte[32];
        Assert.True(y.TryWriteBytes(bytes, out _, isUnsigned: true, isBigEndian: false));
        if (xSign) bytes[31] |= 0x80;
        return bytes;
    }

    private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;

    /// <summary>
    /// Подпись, которая сходится при любом сообщении: ключ A — нейтральная точка (y = 1), S = 0, R = [0]B = нейтральная
    /// точка. Контроль для тестов записи R: с канонической R она проходит (ключ малого порядка — выбор подписанта; у нас
    /// ключ зашит в сборку), поэтому отказ ниже — именно из-за записи R, а не из-за уравнения проверки.
    /// </summary>
    private static byte[] IdentitySignature(byte[] r)
    {
        var sig = new byte[64];
        r.CopyTo(sig, 0);
        return sig;
    }

    [Fact]
    public void Verify_CanonicalIdentityR_Control_Accepted()
    {
        Assert.True(Ed25519.Verify(EncodeY(1), "msg"u8, IdentitySignature(EncodeY(1))), "контроль: каноническая R должна проходить");
    }

    [Theory]
    [InlineData(1, false)] // y = p + 1 ≡ 1 — нейтральная точка в неканонической записи
    [InlineData(1, true)]
    [InlineData(0, false)] // y = p ≡ 0 — точка порядка 4
    [InlineData(18, false)] // y = 2^255 − 1: максимальное 255-битное значение
    public void Verify_NonCanonicalR_YAtLeastP_Rejected(int yMinusP, bool xSign)
    {
        var r = EncodeY(P + yMinusP, xSign);
        Assert.False(Ed25519.Verify(EncodeY(1), "msg"u8, IdentitySignature(r)), $"R с y = p + {yMinusP} (≥ p) принята");
    }

    [Fact]
    public void Verify_XZeroWithSignBit_Rejected()
    {
        // y = 1 и y = p − 1 дают x = 0; запись с установленным знаком x (x = 0, sign = 1) недопустима (RFC 8032, 5.1.3).
        foreach (var y in new[] { BigInteger.One, P - 1 })
        {
            var bad = EncodeY(y, xSign: true);
            Assert.False(Ed25519.Verify(EncodeY(1), "msg"u8, IdentitySignature(bad)), $"R с x = 0 и знаком 1 (y = {y}) принята");
            Assert.False(Ed25519.Verify(bad, "msg"u8, IdentitySignature(EncodeY(1))), $"ключ с x = 0 и знаком 1 (y = {y}) принят");
        }
        // Ключ RFC 8032 с перевёрнутым знаком x — другая точка: подпись не сходится.
        var pk = Hex(Test1.PublicKey);
        pk[31] ^= 0x80;
        Assert.False(Ed25519.Verify(pk, Hex(Test1.Message), Hex(Test1.Signature)));
    }

    [Fact]
    public void Verify_OpenSslSignature_Accepted()
    {
        // Ключ и подпись получены scripts/sign-catalog.ps1 (openssl genpkey -algorithm ed25519; pkeyutl -sign -rawin).
        var pk = Convert.FromBase64String("KMnhbtnkt4TIeOXYkO9CmC67aHfgV8Rp7K/Sq7Dh9I0=");
        var message = Encoding.ASCII.GetBytes("""{ "version": 5, "models": [] }""");
        var sig = Convert.FromBase64String("AM0M+B+IxLitwa2lMn321PxtzVPmCj0mh36mQzcZy7zO+tApsJ1bpA3AYxGbUC5xrPIj5i3e84fsoaww9ns2AA==");
        Assert.True(Ed25519.Verify(pk, message, sig));
        Assert.False(Ed25519.Verify(pk, Encoding.ASCII.GetBytes("""{ "version": 6, "models": [] }"""), sig));
    }
}
