# Сторонние компоненты

## Входят в `Offload.exe`

| Компонент | Лицензия | Источник |
|---|---|---|
| .NET Runtime, Windows Forms (самодостаточная публикация) | MIT | https://github.com/dotnet/runtime, https://github.com/dotnet/winforms |
| ModelContextProtocol (C# SDK) 2.2.0 | Apache-2.0 | https://github.com/modelcontextprotocol/csharp-sdk |
| Microsoft.Extensions.Hosting 10.0 и зависимости | MIT | https://github.com/dotnet/runtime |

## Скачиваются программой при установке

Offload не распространяет эти компоненты, а скачивает их с официальных страниц релизов и проверяет контрольные суммы.

| Компонент | Лицензия | Источник |
|---|---|---|
| llama.cpp (llama-server) | MIT | https://github.com/ggml-org/llama.cpp |
| OpenCode | MIT | https://github.com/anomalyco/opencode |
| ripgrep | MIT / Unlicense | https://github.com/BurntSushi/ripgrep |
| Microsoft Visual C++ Redistributable (при необходимости) | лицензия Microsoft | https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist |
| Модели GGUF | собственные лицензии моделей (Apache-2.0, MIT и др.) | указаны в каталоге `src/Offload.Models/catalog.json` и в окне «Модели» |

## Для сборки и тестов (не входят в exe)

| Компонент | Лицензия |
|---|---|
| xUnit v3 | Apache-2.0 |
| Inno Setup | Inno Setup License |
