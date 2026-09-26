using System.Numerics;
using System.Security.Cryptography;

namespace Offload.Core.Security;

/// <summary>
/// Проверка подписи Ed25519 по RFC 8032 (раздел 5.1.7) — только проверка, подписи здесь не создаются.
/// Реализация на <see cref="BigInteger"/> без внешних зависимостей: медленнее специализированных библиотек
/// (единицы миллисекунд на проверку), зато короткая и проверяемая. Используется для подписанного каталога моделей
/// (раз в сутки), а не для потоков данных. Время выполнения не постоянное — для проверки открытых данных
/// это не важно: секретов в вычислениях нет.
/// </summary>
public static class Ed25519
{
    public const int PublicKeySize = 32;
    public const int SignatureSize = 64;

    /// <summary>Модуль поля p = 2^255 − 19.</summary>
    private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;

    /// <summary>Порядок базовой точки L = 2^252 + 27742317777372353535851937790883648493.</summary>
    private static readonly BigInteger L =
        BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Параметр кривой d = −121665 / 121666 (mod p).</summary>
    private static readonly BigInteger D = Mod(-121665 * Inverse(121666));

    /// <summary>Квадратный корень из −1 (mod p): 2^((p−1)/4).</summary>
    private static readonly BigInteger SqrtMinusOne = BigInteger.ModPow(2, (P - 1) / 4, P);

    /// <summary>Базовая точка B: y = 4/5, x — чётный.</summary>
    private static readonly Point BasePoint = CreateBasePoint();

    private static readonly Point Identity = new(0, 1, 1, 0);

    /// <summary>
    /// Проверить подпись <paramref name="signature"/> (64 байта: R ‖ S) сообщения <paramref name="message"/>
    /// открытым ключом <paramref name="publicKey"/> (32 байта). Любая некорректность входа — false, без исключений.
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != PublicKeySize || signature.Length != SignatureSize) return false;
        if (!TryDecodePoint(publicKey, out var a)) return false;

        var rBytes = signature[..32];
        // R должна быть корректной точкой в канонической записи.
        if (!TryDecodePoint(rBytes, out _)) return false;

        // S — каноническое число меньше L: иначе подпись «пластична» (S + L проходила бы ту же проверку).
        var s = FromLittleEndian(signature[32..]);
        if (s >= L) return false;

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        sha.AppendData(rBytes);
        sha.AppendData(publicKey);
        sha.AppendData(message);
        var k = FromLittleEndian(sha.GetHashAndReset()) % L;

        // [S]B = R + [k]A  ⇔  кодировка [S]B − [k]A совпадает с R (кодировка точки однозначна).
        var check = Add(ScalarMultiply(BasePoint, s), ScalarMultiply(Negate(a), k));
        return Encode(check).AsSpan().SequenceEqual(rBytes);
    }

    // ── Арифметика ─────────────────────────────────────────────────────────────────

    /// <summary>Точка в расширенных координатах (X : Y : Z : T), x = X/Z, y = Y/Z, x·y = T/Z.</summary>
    private readonly record struct Point(BigInteger X, BigInteger Y, BigInteger Z, BigInteger T);

    private static BigInteger Mod(BigInteger a)
    {
        var r = a % P;
        return r.Sign < 0 ? r + P : r;
    }

    private static BigInteger Inverse(BigInteger a) => BigInteger.ModPow(Mod(a), P - 2, P);

    private static BigInteger FromLittleEndian(ReadOnlySpan<byte> bytes) => new(bytes, isUnsigned: true, isBigEndian: false);

    /// <summary>Сложение точек (RFC 8032, 5.1.4) — полная формула, годится и для удвоения.</summary>
    private static Point Add(Point p, Point q)
    {
        var a = Mod((p.Y - p.X) * (q.Y - q.X));
        var b = Mod((p.Y + p.X) * (q.Y + q.X));
        var c = Mod(p.T * 2 * D * q.T);
        var d = Mod(p.Z * 2 * q.Z);
        var e = b - a;
        var f = d - c;
        var g = d + c;
        var h = b + a;
        return new Point(Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h));
    }

    private static Point Negate(Point p) => new(Mod(-p.X), p.Y, p.Z, Mod(-p.T));

    /// <summary>Умножение точки на скаляр (удвоение и сложение, старшие биты первыми).</summary>
    private static Point ScalarMultiply(Point p, BigInteger k)
    {
        var result = Identity;
        for (var i = (int)k.GetBitLength() - 1; i >= 0; i--)
        {
            result = Add(result, result);
            if (!(k >> i).IsEven) result = Add(result, p);
        }
        return result;
    }

    /// <summary>Кодирование точки: y (little-endian, 255 бит) и младший бит x в старшем бите.</summary>
    private static byte[] Encode(Point p)
    {
        var zInv = Inverse(p.Z);
        var x = Mod(p.X * zInv);
        var y = Mod(p.Y * zInv);
        var bytes = new byte[32];
        y.TryWriteBytes(bytes, out _, isUnsigned: true, isBigEndian: false);
        if (!x.IsEven) bytes[31] |= 0x80;
        return bytes;
    }

    /// <summary>Декодирование точки (RFC 8032, 5.1.3). Неканоническая запись (y ≥ p) и точки вне кривой — false.</summary>
    private static bool TryDecodePoint(ReadOnlySpan<byte> encoded, out Point point)
    {
        point = default;
        if (encoded.Length != 32) return false;
        Span<byte> bytes = stackalloc byte[32];
        encoded.CopyTo(bytes);
        var xSign = (bytes[31] >> 7) & 1;
        bytes[31] &= 0x7F;
        var y = FromLittleEndian(bytes);
        if (y >= P) return false;

        var y2 = y * y % P;
        var u = Mod(y2 - 1);
        var v = Mod(D * y2 + 1);
        var v3 = v * v % P * v % P;
        var v7 = v3 * v3 % P * v % P;
        var x = u * v3 % P * BigInteger.ModPow(u * v7 % P, (P - 5) / 8, P) % P;

        var vx2 = v * x % P * x % P;
        if (vx2 == u)
        {
            // x — корень.
        }
        else if (vx2 == Mod(-u))
        {
            x = x * SqrtMinusOne % P;
        }
        else
        {
            return false;
        }

        if (x.IsZero && xSign == 1) return false;
        if ((x.IsEven ? 0 : 1) != xSign) x = P - x;
        point = new Point(x, y, 1, x * y % P);
        return true;
    }

    private static Point CreateBasePoint()
    {
        var y = Mod(4 * Inverse(5));
        var bytes = new byte[32];
        y.TryWriteBytes(bytes, out _, isUnsigned: true, isBigEndian: false);
        return TryDecodePoint(bytes, out var b)
            ? b
            : throw new InvalidOperationException("Ed25519: не удалось построить базовую точку."); // l10n-ignore: внутренняя ошибка, не для интерфейса
    }
}
