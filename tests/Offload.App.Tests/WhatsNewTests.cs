using Offload.App.Services;
using Offload.Core;

namespace Offload.App.Tests;

public sealed class WhatsNewTests
{
    private const string Sample = """
        **Offload** — приложение.

        ## Что нового в 1.2.3

        - **Самообновление.** Проверяет `SHA-256` и подпись,
          см. [README](https://example.com).
        - Исправления.

        Полный список — в CHANGELOG.

        ## Скачать

        - не то

        ---

        ## English

        **New in 1.2.3:** self-update with `SHA-256` checks,
        faster search.

        Run the installer.
        """;

    [Fact]
    public void Russian_BulletsWithContinuation_MarkupRemoved() =>
        Assert.Equal("• Самообновление. Проверяет SHA-256 и подпись, см. README.\n• Исправления.", WhatsNew.Extract(Sample, "1.2.3", english: false));

    [Fact]
    public void English_Paragraph() =>
        Assert.Equal("self-update with SHA-256 checks, faster search.", WhatsNew.Extract(Sample, "1.2.3", english: true));

    [Fact]
    public void OtherVersion_Null()
    {
        Assert.Null(WhatsNew.Extract(Sample, "1.2.4", english: false));
        Assert.Null(WhatsNew.Extract(Sample, "1.2.4", english: true));
    }

    [Theory]
    [InlineData(null, "1.0.2", null)]
    [InlineData("1.0.1", "1.0.2", "1.0.1")]
    [InlineData("1.0.2", "1.0.2", null)]
    [InlineData("1.0.3", "1.0.2", null)]
    public void UpdatedFrom_OnlyWhenNewer(string? last, string current, string? expected) =>
        Assert.Equal(expected, WhatsNew.UpdatedFrom(last, current));

    /// <summary>Страж выпуска: в заметках к релизу (они же встроены в exe) есть «что нового» текущей версии на обоих языках.</summary>
    [Fact]
    public void EmbeddedReleaseNotes_CoverCurrentVersion()
    {
        var md = WhatsNew.Embedded();
        Assert.NotNull(md);
        Assert.NotNull(WhatsNew.Extract(md!, AppInfo.Version, english: false));
        Assert.NotNull(WhatsNew.Extract(md!, AppInfo.Version, english: true));
    }
}
