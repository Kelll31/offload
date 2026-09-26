using Offload.Core.Config;

namespace Offload.Models.Tests;

/// <summary>Поле каталога minLlamaBuild и предупреждение о слишком старой llama.cpp; признак IQ-тензоров установленной модели.</summary>
public sealed class MinLlamaBuildTests
{
    private const string Catalog = """
        { "models": [ { "id": "a", "displayName": "A", "description": "d", "repo": "o/r", "quants": ["Q4_K_M"],
          "files": [ { "quant": "Q4_K_M", "path": "a-Q4_K_M.gguf", "size": 10, "sha256": "0000000000000000000000000000000000000000000000000000000000000000" } ],
          "approxSizeBytes": 10, "paramsB": 1, "activeParamsB": 1, "isMoe": false, "nativeContext": 4096, "defaultContext": 4096,
          "kv": { "layers": 1, "kvHeads": 1, "headDim": 64 }, "goodToolCalling": false,
          "sampling": { "temperature": 0.5 }, "license": "MIT", "minVramGb": 1, "minLlamaBuild": "b11000" } ] }
        """;

    private static AppConfig WithLlama(string? tag) => new() { Llama = { InstalledTag = tag } };

    [Fact]
    public void Parse_ReadsAndValidatesMinLlamaBuild()
    {
        var m = Assert.Single(ModelCatalog.Parse(Catalog));
        Assert.Equal("b11000", m.MinLlamaBuild);
        Assert.Equal(11000, m.MinLlamaBuildNumber);

        Assert.Throws<InvalidDataException>(() => ModelCatalog.Parse(Catalog.Replace("\"b11000\"", "\"11000\"")));
        Assert.Throws<InvalidDataException>(() => ModelCatalog.Parse(Catalog.Replace("\"b11000\"", "\"latest\"")));
        var none = Assert.Single(ModelCatalog.Parse(Catalog.Replace(", \"minLlamaBuild\": \"b11000\"", "")));
        Assert.Null(none.MinLlamaBuild);
        Assert.Equal(0, none.MinLlamaBuildNumber);
    }

    [Fact]
    public void EmbeddedCatalog_MinLlamaBuildIsWellFormed()
    {
        foreach (var m in ModelCatalog.All.Where(m => m.MinLlamaBuild is not null))
            Assert.True(m.MinLlamaBuildNumber > 0, $"{m.Id}: неверный minLlamaBuild");
    }

    [Theory]
    [InlineData("b10999", true)]
    [InlineData("b6500", true)]
    [InlineData("b11000", false)]
    [InlineData("b11102", false)]
    [InlineData(null, false)]
    [InlineData("custom", false)]
    public void LlamaBuildWarning_OnlyWhenInstalledBuildIsOlder(string? installed, bool warns)
    {
        var model = Assert.Single(ModelCatalog.Parse(Catalog));
        var warning = ModelManager.LlamaBuildWarning(model, WithLlama(installed));
        if (!warns)
        {
            Assert.Null(warning);
            return;
        }
        Assert.NotNull(warning);
        Assert.Contains("b11000", warning);
        Assert.Contains(installed!, warning);
    }

    [Fact]
    public void LlamaBuildWarning_NoRequirement_Null()
    {
        var model = ModelCatalog.All[0] with { MinLlamaBuild = null };
        Assert.Null(ModelManager.LlamaBuildWarning(model, WithLlama("b1")));
        // Модель встроенного каталога без требования / неизвестная модель.
        Assert.Null(ModelManager.LlamaBuildWarning(ModelCatalog.All[0].Id, WithLlama("b1")));
        Assert.Null(ModelManager.LlamaBuildWarning("no-such-model", WithLlama("b1")));
    }

    [Fact]
    public void HasIqTensors_FromCatalogQuant()
    {
        var model = ModelCatalog.All.First(m => m.Files!.Any(f => f.IqTensors) && m.Files!.Any(f => !f.IqTensors));
        var iq = model.Files!.First(f => f.IqTensors).Quant;
        var plain = model.Files!.First(f => !f.IqTensors).Quant;

        Assert.True(ModelCatalog.HasIqTensors(new InstalledModel { Id = model.Id, Quant = iq }));
        Assert.False(ModelCatalog.HasIqTensors(new InstalledModel { Id = model.Id, Quant = plain }));
        Assert.Null(ModelCatalog.HasIqTensors(new InstalledModel { Id = model.Id, Quant = "NO-SUCH" }));
        Assert.Null(ModelCatalog.HasIqTensors(new InstalledModel { Id = model.Id, Quant = iq, IsCustom = true }));
        Assert.Null(ModelCatalog.HasIqTensors(new InstalledModel { Id = "custom-x" }));
        Assert.Null(ModelCatalog.HasIqTensors(null));
    }
}
