namespace Offload.Core.Ipc;

/// <summary>
/// Роль модели в команде start-server (ROADMAP §5.2). Совместимо в обе стороны без смены версии протокола:
/// старый клиент не передаёт роль — трей запускает основной сервер (quality); старый трей аргумент не знает и отвечает
/// без <see cref="Role"/> в Data — клиент по этому понимает, что вспомогательные серверы трею неизвестны.
/// </summary>
public static class IpcRoleArgs
{
    /// <summary>Args["role"] запроса и Data["role"] ответа: quality, fast, embed, rerank.</summary>
    public const string Role = "role";

    /// <summary>Data["roleBaseUrl"] ответа: адрес сервера роли (порт мог смениться, если был занят).</summary>
    public const string BaseUrl = "roleBaseUrl";

    /// <summary>Data["error"] ответа, когда роли не назначена модель.</summary>
    public const string Error = "error";

    public const string RoleNotAssigned = "role-not-assigned";
}
