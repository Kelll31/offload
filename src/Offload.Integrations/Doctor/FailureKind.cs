namespace Offload.Integrations;

/// <summary>Причина, по которой подключение к IDE не проверено или не работает (для понятного сообщения пользователю).</summary>
public enum FailureKind
{
    None,
    /// <summary>Файл конфигурации IDE не найден.</summary>
    ConfigMissing,
    /// <summary>Файл конфигурации занят другой программой или недоступен для записи.</summary>
    ConfigLocked,
    /// <summary>Файл конфигурации не разобран (битый JSON, не UTF-8): Offload его не трогает.</summary>
    ConfigInvalid,
    /// <summary>Offload.exe из записи отсутствует.</summary>
    ExeMissing,
    /// <summary>Запуск exe запрещён (антивирус, политика, права).</summary>
    ExeBlocked,
    /// <summary>Процесс не стартовал или завершился, не ответив.</summary>
    StartFailed,
    /// <summary>В stdout сервера попал посторонний текст — IDE может отключить такой сервер.</summary>
    ProtocolNoise,
    /// <summary>Сервер не ответил за отведённое время.</summary>
    Timeout,
    /// <summary>IDE не найдена на компьютере.</summary>
    ClientNotFound,
    /// <summary>Для этой IDE автоматической проверки нет (подключение выполняется вручную).</summary>
    NotVerifiable,
}

/// <summary>Понятные тексты причин: что случилось и что делать.</summary>
public static class FailureText
{
    public static string Reason(FailureKind kind) => kind switch
    {
        FailureKind.ConfigMissing => L.T("файл конфигурации IDE не найден"),
        FailureKind.ConfigLocked => L.T("файл конфигурации занят другой программой"),
        FailureKind.ConfigInvalid => L.T("файл конфигурации IDE повреждён или не разобран"),
        FailureKind.ExeMissing => L.T("Offload.exe по пути из записи не найден"),
        FailureKind.ExeBlocked => L.T("запуск Offload.exe запрещён (антивирус или политика Windows)"),
        FailureKind.StartFailed => L.T("сервер Offload не запустился или закрылся, не ответив"),
        FailureKind.ProtocolNoise => L.T("в stdout сервера попал посторонний текст"),
        FailureKind.Timeout => L.T("сервер Offload не ответил вовремя"),
        FailureKind.ClientNotFound => L.T("IDE не найдена на этом компьютере"),
        FailureKind.NotVerifiable => L.T("для этой IDE автоматической проверки нет"),
        _ => "",
    };

    public static string Hint(FailureKind kind) => kind switch
    {
        FailureKind.ConfigMissing => L.T("Запустите IDE один раз, чтобы она создала конфигурацию, и нажмите «Проверить»."),
        FailureKind.ConfigLocked => L.T("Закройте IDE и нажмите «Проверить»."),
        FailureKind.ConfigInvalid => L.T("Исправьте ошибку в файле вручную (резервная копия рядом) и нажмите «Проверить»; Offload файл не менял."),
        FailureKind.ExeMissing => L.T("Нажмите «Обновить путь» на странице «Интеграции» или переустановите Offload."),
        FailureKind.ExeBlocked => L.T("Добавьте Offload.exe в исключения антивируса и нажмите «Проверить»."),
        FailureKind.StartFailed => L.T("Откройте журнал Offload, проверьте, что установлен Visual C++ Redistributable, и нажмите «Проверить»."),
        FailureKind.ProtocolNoise => L.T("Обновите Offload до последней версии; если проблема осталась, приложите журнал к обращению."),
        FailureKind.Timeout => L.T("Первый запуск может быть медленным из-за антивируса — нажмите «Проверить» ещё раз."),
        FailureKind.ClientNotFound => L.T("Установите IDE и нажмите «Проверить»."),
        _ => "",
    };
}
