namespace Offload.Integrations;

/// <summary>Состояние нашего управляемого текста (файл-дополнение или секция в чужом файле).</summary>
public enum ManagedState
{
    /// <summary>Не установлен.</summary>
    Missing,
    /// <summary>Установлен и совпадает с текстом этой версии Offload.</summary>
    Current,
    /// <summary>Установлен прежней версией Offload и не менялся пользователем — можно обновить.</summary>
    Outdated,
    /// <summary>Установлен Offload, но изменён пользователем — автоматически не обновляется.</summary>
    Modified,
    /// <summary>Файл есть, но создан не Offload (нет метки) — не трогаем.</summary>
    Foreign,
}
