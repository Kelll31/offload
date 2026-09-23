using System.Globalization;
using System.Text.RegularExpressions;
using Offload.Core.Config;
using Offload.Core.Hardware;

namespace Offload.Llama;

/// <summary>
/// Выбор сборки llama.cpp под оборудование.
/// Правило для NVIDIA (сентябрь 2026): основная сборка — CUDA 12.4 (в ней есть готовый код для sm_86/sm_89,
/// остальные карты от Maxwell до Hopper работают через PTX JIT). CUDA 13 — только для Blackwell (cc ≥ 10),
/// которого нет в сборке 12.4: в CUDA 13.x замечены ошибки вычислений с IQ-квантами (llama.cpp #21255),
/// а в каталоге много UD-квантов unsloth с IQ-тензорами. Старый драйвер → Vulkan.
/// </summary>
internal static class BackendAdvisor
{
    /// <summary>CUDA 12.x для карт с готовым кодом (minor-version compatibility, Windows 12.0 GA).</summary>
    internal static readonly Version MinDriverCuda12Native = new(527, 41);

    /// <summary>CUDA 12.4 Update 1 — нужен для PTX JIT (Maxwell/Pascal/Volta/Turing/A100/H100).</summary>
    internal static readonly Version MinDriverCuda12Ptx = new(551, 78);

    /// <summary>CUDA 13.x minor-version compatibility (ветка R580).</summary>
    internal static readonly Version MinDriverCuda13 = new(580, 0);

    /// <summary>Архитектуры с готовым машинным кодом (SASS) в сборке CUDA 12.4.</summary>
    private static readonly Version[] Cuda12Native = [new(8, 6), new(8, 9)];

    private static string IqNote =>
        L.T("CUDA 13 не выбрана: в сборках CUDA 13.x замечены ошибки вычислений с IQ-квантами (llama.cpp #21255), а они есть во многих моделях каталога.");

    public static BackendRecommendation Recommend(HardwareInfo hw)
    {
        ArgumentNullException.ThrowIfNull(hw);
        var nvidia = NvidiaGpus(hw);
        var driver = NvidiaDriver(hw);

        if (hw.IsArm64)
        {
            if (nvidia.Count > 0 && driver is not null && driver >= MinDriverCuda13)
                return new(LlamaBackend.Cuda13,
                    L.F("Windows на ARM с видеокартой {0}: для ARM64 есть только сборка CUDA 13.", nvidia[0].Name));
            return new(LlamaBackend.Cpu, L.T("Windows на ARM: используется сборка llama.cpp для процессора ARM64."));
        }

        var primary = hw.PrimaryGpu;
        var discrete = primary is { IsIntegrated: false };
        if (nvidia.Count > 0 && (!discrete || primary!.Vendor == GpuVendor.Nvidia))
            return RecommendNvidia(nvidia, driver);

        if (primary is null || !discrete)
        {
            return primary is null
                ? new(LlamaBackend.Cpu, L.T("Видеокарта не найдена — модель будет работать на процессоре."))
                : new(LlamaBackend.Cpu,
                    L.F("Найдена только встроенная графика ({0}) — модель будет работать на процессоре. Сборку Vulkan можно выбрать вручную.",
                        primary.Name));
        }

        return primary.Vendor switch
        {
            GpuVendor.Amd => new(LlamaBackend.Vulkan,
                L.F("Видеокарта AMD {0}: выбрана сборка Vulkan — работает с любым драйвером Radeon без дополнительных компонентов и надёжнее с вызовом инструментов (в сборке ROCm бывают сбои, llama.cpp #27612).",
                    Short(primary.Name)) +
                (IsRocmCapable(primary.Name) ? " " + L.T("Сборку AMD ROCm можно выбрать вручную.") : "")),
            GpuVendor.Intel => new(LlamaBackend.Vulkan,
                L.F("Видеокарта Intel {0}: выбрана сборка Vulkan. Сборку Intel SYCL можно выбрать вручную.", Short(primary.Name))),
            _ => new(LlamaBackend.Vulkan, L.F("Видеокарта {0}: выбрана универсальная сборка Vulkan.", primary.Name)),
        };
    }

    private static BackendRecommendation RecommendNvidia(List<GpuInfo> gpus, Version? driver)
    {
        var name = gpus[0].Name;
        var ccs = gpus.Select(ComputeCapability).ToList();
        var drv = driver is null ? L.T("неизвестен") : FormatDriver(driver);

        // Blackwell (sm_100/sm_120) есть только в сборке CUDA 13.
        if (ccs.Any(c => c is not null && c.Major >= 10))
        {
            var bw = gpus.First(g => ComputeCapability(g) is { Major: >= 10 }).Name;
            if (driver is null || driver >= MinDriverCuda13)
                return new(LlamaBackend.Cuda13,
                    L.F("Видеокарта {0} (Blackwell) поддерживается только сборкой CUDA 13 (драйвер {1}).", bw, drv));
            return new(LlamaBackend.Vulkan,
                L.F("Для видеокарты {0} нужна сборка CUDA 13 и драйвер NVIDIA 580 или новее (установлен {1}). Пока выбрана сборка Vulkan — обновите драйвер, чтобы получить максимальную скорость.",
                    bw, drv));
        }

        if (ccs.Any(c => c is not null && c < new Version(5, 0)))
            return new(LlamaBackend.Vulkan,
                L.F("Видеокарта {0} слишком старая для сборок CUDA (нужна архитектура Maxwell или новее) — выбрана сборка Vulkan.", name));

        var allNative = ccs.All(c => c is not null && Cuda12Native.Contains(new Version(c.Major, c.Minor)));
        var need = allNative ? MinDriverCuda12Native : MinDriverCuda12Ptx;

        if (driver is null)
            return new(LlamaBackend.Cuda12,
                L.F("Видеокарта {0}: выбрана сборка CUDA 12.4. Версию драйвера определить не удалось — нужен драйвер {1} или новее. {2}",
                    name, FormatDriver(need), IqNote));

        if (driver < need)
            return new(LlamaBackend.Vulkan,
                L.F("Драйвер NVIDIA {0} слишком старый для сборки CUDA 12.4 (нужен {1} или новее) — выбрана сборка Vulkan. Обновите драйвер, чтобы получить максимальную скорость.",
                    drv, FormatDriver(need)));

        var cc = ccs[0] is { } c0 ? $" (compute capability {c0.Major}.{c0.Minor})" : "";
        return allNative
            ? new(LlamaBackend.Cuda12,
                L.F("Видеокарта {0}{1}, драйвер {2}: выбрана сборка CUDA 12.4 — в ней есть готовый код для этой карты. {3}", name, cc, drv, IqNote))
            : new(LlamaBackend.Cuda12,
                L.F("Видеокарта {0}{1}, драйвер {2}: выбрана сборка CUDA 12.4. Ядра для этой карты компилирует драйвер, поэтому первый запуск может занять несколько минут. {3}",
                    name, cc, drv, IqNote));
    }

    public static IReadOnlyList<LlamaBackend> Available(HardwareInfo hw)
    {
        ArgumentNullException.ThrowIfNull(hw);
        var list = new List<LlamaBackend>();
        var nvidia = NvidiaGpus(hw);

        if (hw.IsArm64)
        {
            if (nvidia.Count > 0) list.Add(LlamaBackend.Cuda13);
            list.Add(LlamaBackend.Cpu);
            return list;
        }

        if (nvidia.Count > 0)
        {
            var ccs = nvidia.Select(ComputeCapability).ToList();
            var anyBlackwell = ccs.Any(c => c is { Major: >= 10 });
            var anyKepler = ccs.Any(c => c is not null && c < new Version(5, 0));
            var cuda13Capable = ccs.All(c => c is null || c >= new Version(7, 5));
            if (!anyBlackwell && !anyKepler) list.Add(LlamaBackend.Cuda12);
            if (cuda13Capable && !anyKepler) list.Add(LlamaBackend.Cuda13);
        }

        if (hw.Gpus.Count > 0) list.Add(LlamaBackend.Vulkan);
        if (hw.Gpus.Any(g => g.Vendor == GpuVendor.Amd && IsRocmCapable(g.Name))) list.Add(LlamaBackend.Rocm);
        if (hw.Gpus.Any(g => g.Vendor == GpuVendor.Intel && (!g.IsIntegrated || g.Name.Contains("Arc", StringComparison.OrdinalIgnoreCase))))
            list.Add(LlamaBackend.Sycl);
        list.Add(LlamaBackend.Cpu);

        var rec = Recommend(hw).Backend;
        if (!list.Contains(rec)) list.Insert(0, rec);
        return list;
    }

    /// <summary>Подходит ли оборудование для сборки CUDA 13 (запасной вариант, если в релизе нет CUDA 12).</summary>
    public static bool SupportsCuda13(HardwareInfo hw)
    {
        var nvidia = NvidiaGpus(hw);
        if (nvidia.Count == 0) return false;
        var driver = NvidiaDriver(hw);
        if (driver is not null && driver < MinDriverCuda13) return false;
        return nvidia.All(g => ComputeCapability(g) is not { } c || c >= new Version(7, 5));
    }

    public static string DisplayName(LlamaBackend backend) => backend switch
    {
        LlamaBackend.Auto => L.T("Автовыбор"),
        LlamaBackend.Cuda12 => "NVIDIA CUDA 12.4",
        LlamaBackend.Cuda13 => L.T("NVIDIA CUDA 13 (для RTX 50xx)"),
        LlamaBackend.Vulkan => L.T("Vulkan (любая видеокарта)"),
        LlamaBackend.Rocm => "AMD ROCm (HIP)",
        LlamaBackend.Sycl => "Intel SYCL (oneAPI)",
        LlamaBackend.Cpu => L.T("Только процессор"),
        _ => backend.ToString(),
    };

    private static List<GpuInfo> NvidiaGpus(HardwareInfo hw) =>
        hw.Gpus.Where(g => g.Vendor == GpuVendor.Nvidia && !g.IsIntegrated)
            .OrderByDescending(g => g.DedicatedMemoryBytes)
            .ToList();

    private static Version? NvidiaDriver(HardwareInfo hw) =>
        hw.Gpus.Where(g => g.Vendor == GpuVendor.Nvidia).Select(g => ParseDriver(g.DriverVersion)).FirstOrDefault(v => v is not null);

    /// <summary>
    /// Версия драйвера NVIDIA: «616.92» (nvidia-smi) или формат Windows «32.0.15.6109» (реестр) → 561.09.
    /// </summary>
    internal static Version? ParseDriver(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var parts = s.Trim().Split('.');
        if (parts.Length == 4 && parts.All(p => p.All(char.IsAsciiDigit) && p.Length > 0) && int.Parse(parts[0], CultureInfo.InvariantCulture) >= 10)
        {
            // Последние 5 цифр «build.revision» — версия NVIDIA: 15.6109 → 56109 → 561.09.
            var digits = parts[2] + parts[3].PadLeft(4, '0');
            if (digits.Length < 5) return null;
            digits = digits[^5..];
            return new Version(int.Parse(digits[..3], CultureInfo.InvariantCulture), int.Parse(digits[3..], CultureInfo.InvariantCulture));
        }
        var m = Regex.Match(s, @"^\s*(\d{2,4})(?:\.(\d{1,2}))?");
        if (!m.Success) return null;
        var minor = m.Groups[2].Success ? m.Groups[2].Value : "0";
        if (minor.Length == 1 && m.Groups[2].Success) minor += "0";
        return new Version(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(minor, CultureInfo.InvariantCulture));
    }

    private static string FormatDriver(Version v) => $"{v.Major}.{Math.Max(0, v.Minor):00}";

    /// <summary>Compute capability из nvidia-smi, а если её нет (данные реестра) — по названию карты.</summary>
    internal static Version? ComputeCapability(GpuInfo g)
    {
        if (!string.IsNullOrWhiteSpace(g.ComputeCapability)
            && Version.TryParse(g.ComputeCapability.Trim(), out var v))
            return new Version(v.Major, Math.Max(0, v.Minor));
        return GuessComputeCapability(g.Name);
    }

    internal static Version? GuessComputeCapability(string name)
    {
        var n = name.ToUpperInvariant();
        bool Has(string pattern) => Regex.IsMatch(n, pattern, RegexOptions.CultureInvariant);
        if (Has(@"RTX\s*50\d\d") || Has(@"BLACKWELL") || Has(@"\bB[12]00\b") || Has(@"\bGB10\b")) return new Version(12, 0);
        if (Has(@"RTX\s*40\d\d") || Has(@"RTX\s*\d{4}\s*ADA") || Has(@"\bL4\b") || Has(@"\bL40")) return new Version(8, 9);
        if (Has(@"RTX\s*30\d\d") || Has(@"RTX\s*A\d{3,4}") || Has(@"\bA(2|10|16|30|40)\b")) return new Version(8, 6);
        if (Has(@"\bA100\b") || Has(@"\bA800\b")) return new Version(8, 0);
        if (Has(@"\bH100\b") || Has(@"\bH200\b") || Has(@"\bGH200\b")) return new Version(9, 0);
        if (Has(@"RTX\s*20\d\d") || Has(@"GTX\s*16\d\d") || Has(@"TITAN\s*RTX") || Has(@"QUADRO\s*RTX") || Has(@"\bT4\b")
            || Has(@"\bT(400|600|1000|1200|2000)\b") || Has(@"MX\s*[45]\d0")) return new Version(7, 5);
        if (Has(@"TITAN\s*V\b") || Has(@"\bV100\b")) return new Version(7, 0);
        if (Has(@"GTX\s*10\d\d") || Has(@"TITAN\s*XP") || Has(@"QUADRO\s*P\d") || Has(@"\bP(4|40|100)\b") || Has(@"MX\s*[123]\d0")) return new Version(6, 1);
        if (Has(@"GTX\s*9\d\d") || Has(@"GTX\s*TITAN\s*X\b") || Has(@"GTX\s*750") || Has(@"QUADRO\s*M\d")) return new Version(5, 2);
        if (Has(@"GTX\s*[67]\d\d") || Has(@"GT\s*7\d\d") || Has(@"TITAN\s*(BLACK|Z)\b")) return new Version(3, 5);
        return null;
    }

    /// <summary>
    /// Карты, для которых собран win-rocm (gfx1010–1012, gfx1030–1036, gfx1100–1103, gfx1150–1153, gfx1200–1201):
    /// Radeon RX 5000–9000, Radeon PRO W5000–W9000, встроенные 660M–890M и 8040S–8060S.
    /// </summary>
    internal static bool IsRocmCapable(string name)
    {
        var n = name.ToUpperInvariant();
        return Regex.IsMatch(n, @"\bRX\s*[5679]\d{3}\b", RegexOptions.CultureInvariant)
               || Regex.IsMatch(n, @"\bW[5679]\d{3}\b", RegexOptions.CultureInvariant)
               || Regex.IsMatch(n, @"RADEON\s*(\(TM\)\s*)?[678]\d0M\b", RegexOptions.CultureInvariant)
               || Regex.IsMatch(n, @"RADEON\s*(\(TM\)\s*)?80[4-6]0S\b", RegexOptions.CultureInvariant);
    }

    private static string Short(string name)
    {
        var s = name.Replace("(TM)", "", StringComparison.OrdinalIgnoreCase).Replace("(R)", "", StringComparison.OrdinalIgnoreCase);
        foreach (var prefix in new[] { "AMD ", "Intel ", "NVIDIA " })
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) s = s[prefix.Length..];
        return Regex.Replace(s, @"\s{2,}", " ").Trim();
    }
}
