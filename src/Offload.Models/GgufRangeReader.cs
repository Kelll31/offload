namespace Offload.Models;

/// <summary>
/// Чтение заголовка GGUF по частям (HTTP Range) без загрузки весов: первые мегабайты файла дочитываются, пока
/// разбор не дойдёт до конца метаданных. Заголовок бывает большим (словарь токенизатора, merges — несколько МБ),
/// поэтому объём ограничен <see cref="MaxHeaderBytes"/>.
/// </summary>
public static class GgufRangeReader
{
    /// <summary>Первая порция: у большинства моделей заголовок меньше.</summary>
    public const int InitialBytes = 4 * 1024 * 1024;

    /// <summary>Больше этого заголовок не читается (защита от бесконечной загрузки).</summary>
    public const int MaxHeaderBytes = 32 * 1024 * 1024;

    /// <summary>
    /// Прочитать заголовок: <paramref name="fetch"/>(смещение, длина) возвращает байты файла с этого смещения
    /// (не больше длины; меньше — только в конце файла). Возвращает метаданные и сколько байт пришлось скачать.
    /// </summary>
    /// <exception cref="InvalidDataException">Файл не GGUF, повреждён или заголовок больше <paramref name="maxBytes"/>.</exception>
    public static async Task<(GgufInfo Info, long BytesRead)> ReadAsync(
        Func<long, int, CancellationToken, Task<byte[]>> fetch, long totalLength,
        int initialBytes = InitialBytes, int maxBytes = MaxHeaderBytes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        if (totalLength <= 0) throw new InvalidDataException(L.T("Размер файла модели неизвестен — заголовок GGUF не прочитать."));
        var limit = (int)Math.Min(maxBytes, totalLength);
        var buffer = new byte[Math.Min(Math.Max(1024, initialBytes), limit)];
        var have = 0;
        var want = buffer.Length;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            while (have < want)
            {
                var chunk = await fetch(have, want - have, ct).ConfigureAwait(false);
                if (chunk.Length == 0) throw new InvalidDataException(L.T("Сервер оборвал чтение заголовка GGUF."));
                var n = Math.Min(chunk.Length, want - have);
                Buffer.BlockCopy(chunk, 0, buffer, have, n);
                have += n;
            }
            try
            {
                return (GgufReader.Read(new PrefixStream(buffer, have, totalLength)), have);
            }
            catch (NeedMoreDataException more)
            {
                if (have >= limit)
                {
                    throw new InvalidDataException(totalLength <= maxBytes
                        ? L.T("Заголовок GGUF обрывается — файл неполный или повреждён.")
                        : L.F("Заголовок GGUF больше {0} МБ — метаданные модели не прочитаны.", maxBytes / (1024 * 1024)));
                }
                // Удвоение (или сразу до нужного места с запасом 1 МБ), не больше предела.
                want = (int)Math.Min(limit, Math.Max((long)have * 2, more.Required + 1024 * 1024));
                if (want > buffer.Length) Array.Resize(ref buffer, want);
            }
            catch (EndOfStreamException ex)
            {
                throw new InvalidDataException(L.T("Заголовок GGUF обрывается — файл неполный или повреждён."), ex);
            }
        }
    }

    /// <summary>Прочитанных байт не хватило: разбор дошёл до позиции Required.</summary>
    internal sealed class NeedMoreDataException(long required) : Exception
    {
        public long Required { get; } = required;
    }

    /// <summary>
    /// Поток «начало файла»: длина — полный размер файла (для проверок границ в <see cref="GgufReader"/>), данные — только
    /// первые <c>available</c> байт; чтение дальше — <see cref="NeedMoreDataException"/>.
    /// </summary>
    internal sealed class PrefixStream(byte[] data, int available, long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => _position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            if (count <= 0 || _position >= length) return 0;
            if (_position >= available) throw new NeedMoreDataException(_position + count);
            var n = (int)Math.Min(count, available - _position);
            Buffer.BlockCopy(data, (int)_position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => length + offset,
            };
            if (target < 0) throw new IOException(L.T("Перемещение перед началом файла."));
            _position = target;
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
