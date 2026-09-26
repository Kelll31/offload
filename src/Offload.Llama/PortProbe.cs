using System.Net;
using System.Net.Sockets;
using Offload.Core;
using Offload.Core.Logging;

namespace Offload.Llama;

/// <summary>Проверка занятости TCP-порта и выбор свободного.</summary>
internal static class PortProbe
{
    public const int SearchRange = 50;

    public static int ChooseFreePort(string host, int port)
    {
        if (port is <= 0 or > 65535) port = 8765;
        var listening = ListeningPorts();
        if (IsFree(host, port, listening)) return port;
        var last = Math.Min(65535, port + SearchRange);
        for (var p = port + 1; p <= last; p++)
        {
            if (IsFree(host, p, listening)) return p;
        }
        throw new LlamaServerException(
            L.F("Порт {0} занят, и среди портов {1}–{2} нет свободного. Укажите другой порт в настройках сервера.", port, port + 1, last));
    }

    public static bool IsFree(string host, int port) => IsFree(host, port, ListeningPorts());

    /// <summary>
    /// Порт свободен: его никто не слушает (на любом адресе — таблица TCP-слушателей) и его удаётся занять
    /// (не входит в зарезервированные Windows диапазоны, не занят эксклюзивно).
    /// </summary>
    private static bool IsFree(string host, int port, HashSet<int> listening)
    {
        if (listening.Contains(port)) return false;
        try
        {
            var l = new TcpListener(BindAddress(host), port) { ExclusiveAddressUse = false };
            l.Start();
            l.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static HashSet<int> ListeningPorts()
    {
        try
        {
            return System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Select(ep => ep.Port)
                .ToHashSet();
        }
        catch (Exception ex)
        {
            Log.Debug("llama", $"Список TCP-слушателей недоступен: {ex.Message}");
            return [];
        }
    }

    private static IPAddress BindAddress(string host)
    {
        var h = (host ?? "").Trim().Trim('[', ']');
        if (h.Length == 0 || h.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return IPAddress.Loopback;
        if (h is "*" or "+" or "0.0.0.0") return IPAddress.Any;
        return IPAddress.TryParse(h, out var ip) ? ip : IPAddress.Loopback;
    }
}
