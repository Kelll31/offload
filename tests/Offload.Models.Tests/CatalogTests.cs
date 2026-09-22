using System.Text.RegularExpressions;
using Offload.Core.Config;

namespace Offload.Models.Tests;

public class CatalogTests
{
    private const long MiB = 1024 * 1024;

    [Fact]
    public void All_LoadsSortedAndUnique()
    {
        var all = ModelCatalog.All;
        Assert.InRange(all.Count, 12, 16);
        Assert.Equal(all.Count, all.Select(m => m.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(all.OrderBy(m => m.Priority).Select(m => m.Id), all.Select(m => m.Id));
        Assert.Same(all, ModelCatalog.All); // ленивый кэш
        Assert.Equal("qwen3.8-27b-q4", all[0].Id);
    }

    [Fact]
    public void Find_IsCaseInsensitive()
    {
        Assert.NotNull(ModelCatalog.Find("GPT-OSS-20B"));
        Assert.Null(ModelCatalog.Find("no-such-model"));
    }

    [Fact]
    public void EveryModel_HasCompleteDefaultFile()
    {
        foreach (var m in ModelCatalog.All)
        {
            var def = m.DefaultFile;
            Assert.NotNull(def);
            Assert.Equal(m.Quants[0], def!.Quant);
            Assert.True(def.Size > 100 * MiB, m.Id);
            Assert.Matches("^[0-9a-f]{64}$", def.Sha256!);
            Assert.Equal(def.Size, m.ApproxSizeBytes);
            Assert.Matches("^[0-9a-f]{40}$", m.Revision!);
            Assert.EndsWith(".gguf", def.Path);
            Assert.DoesNotContain("/", def.Path); // ни один рекомендованный файл не разбит на шарды
            foreach (var q in m.Quants)
            {
                var f = m.FindFile(q);
                Assert.NotNull(f);
                Assert.Matches("^[0-9a-f]{64}$", f!.Sha256!);
                Assert.True(f.Size > 0);
                Assert.Equal(q, HfClient.QuantTag(f.Path), StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void EveryModel_HasRussianTextsAndSaneSettings()
    {
        var cyr = new Regex("[А-Яа-яЁё]");
        foreach (var m in ModelCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(m.DisplayName), m.Id);
            Assert.Matches(cyr, m.Description);
            Assert.Contains("Лицензия", m.Description);
            Assert.InRange(m.Description.Length, 120, 700);
            Assert.False(string.IsNullOrWhiteSpace(m.License));
            Assert.False(string.IsNullOrWhiteSpace(m.Architecture));
            Assert.True(m.BlockCount > 0, m.Id);
            Assert.True(m.DefaultContext <= m.NativeContext, m.Id);
            Assert.InRange(m.Sampling.Temperature, 0.1, 1.5);
            Assert.InRange(m.Sampling.TopP, 0.5, 1.0);
            Assert.True(m.ParamsB >= m.ActiveParamsB, m.Id);
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", m.ReleaseDate!);
        }
    }

    /// <summary>Байт KV-кэша на токен (fp16) — таблица из исследования (раздел 4).</summary>
    [Theory]
    [InlineData("qwen3.6-35b-a3b-q4", 20480, 63)]
    [InlineData("ornith-1.5-35b-a3b-q4", 20480, 63)]
    [InlineData("kat-coder-v2.5-dev-q4", 20480, 63)]
    [InlineData("qwen3.8-27b-q4", 65536, 150)]
    [InlineData("qwen3.8-27b-iq3", 65536, 150)]
    [InlineData("qwen3.8-27b-q6", 65536, 150)]
    [InlineData("qwen3-coder-next-q4", 24576, 75)]
    [InlineData("ornith-1.5-9b-q4", 32768, 50)]
    [InlineData("qwen3.5-9b-q4", 32768, 50)]
    [InlineData("qwen3.5-4b-q4", 32768, 50)]
    [InlineData("qwen3.5-2b-q8", 12288, 19)]
    [InlineData("gpt-oss-20b", 24576, 0)]
    [InlineData("devstral-small-2-q4", 163840, 0)]
    public void KvBytesPerToken_MatchesResearch(string id, int fp16BytesPerToken, int stateMiB)
    {
        var m = ModelCatalog.Find(id);
        Assert.NotNull(m);
        Assert.Equal(fp16BytesPerToken, FitCalculator.KvBytesPerToken(m!.Kv, "f16"));
        Assert.Equal(fp16BytesPerToken * 34 / 64, FitCalculator.KvBytesPerToken(m.Kv, "q8_0"));
        Assert.Equal(stateMiB * MiB, m.Kv.RecurrentStateBytes);
    }

    [Fact]
    public void MoeModels_HaveConsistentExpertSplit()
    {
        foreach (var m in ModelCatalog.All)
        {
            if (!m.IsMoe)
            {
                Assert.Null(m.Moe);
                continue;
            }
            Assert.NotNull(m.Moe);
            Assert.Equal(m.BlockCount, m.Moe!.MoeLayers);
            // Эксперты + прочие тензоры ≈ размер файла (заголовок GGUF — ~10 МБ).
            var tensors = m.Moe.MoeLayers * m.Moe.ExpertBytesPerLayer + m.Moe.NonExpertBytes;
            Assert.InRange(m.ApproxSizeBytes - tensors, 0, 30 * MiB);
            foreach (var f in m.Files!.Where(f => f.Moe is not null))
            {
                var t = f.Moe!.MoeLayers * f.Moe.ExpertBytesPerLayer + f.Moe.NonExpertBytes;
                Assert.InRange(f.Size - t, 0, 30 * MiB);
            }
        }
    }

    [Theory]
    [InlineData("qwen3.6-35b-a3b-q4", ReasoningControl.EnableThinkingKwarg, true, false)]
    [InlineData("qwen3.8-27b-q4", ReasoningControl.EnableThinkingKwarg, true, true)]
    [InlineData("ornith-1.5-35b-a3b-q4", ReasoningControl.EnableThinkingKwarg, true, true)]
    [InlineData("ornith-1.5-9b-q4", ReasoningControl.EnableThinkingKwarg, true, true)]
    [InlineData("kat-coder-v2.5-dev-q4", ReasoningControl.EnableThinkingKwarg, true, false)]
    [InlineData("gpt-oss-20b", ReasoningControl.ReasoningEffort, true, false)]
    [InlineData("qwen3-coder-next-q4", ReasoningControl.None, false, false)]
    [InlineData("devstral-small-2-q4", ReasoningControl.None, false, false)]
    [InlineData("qwen3.5-2b-q8", ReasoningControl.EnableThinkingKwarg, false, false)]
    public void ReasoningAndMtp_MatchResearch(string id, ReasoningControl reasoning, bool thinks, bool mtp)
    {
        var m = ModelCatalog.Find(id)!;
        Assert.Equal(reasoning, m.Reasoning);
        Assert.Equal(thinks, m.IsReasoning);
        Assert.Equal(mtp, m.HasMtp);
    }

    [Fact]
    public void Sampling_ForNonThinkingDelegation()
    {
        var qwen = ModelCatalog.Find("qwen3.6-35b-a3b-q4")!.Sampling;
        Assert.Equal((0.7, 0.8, 20, 0.0, 1.5), (qwen.Temperature, qwen.TopP, qwen.TopK, qwen.MinP, qwen.PresencePenalty));
        var ornith = ModelCatalog.Find("ornith-1.5-35b-a3b-q4")!.Sampling;
        Assert.Equal((0.6, 0.95, 20), (ornith.Temperature, ornith.TopP, ornith.TopK));
        var oss = ModelCatalog.Find("gpt-oss-20b")!.Sampling;
        Assert.Equal((1.0, 1.0, 0), (oss.Temperature, oss.TopP, oss.TopK));
        var next = ModelCatalog.Find("qwen3-coder-next-q4")!.Sampling;
        Assert.Equal((1.0, 0.95, 40, 0.01), (next.Temperature, next.TopP, next.TopK, next.MinP));
        Assert.Equal(0.15, ModelCatalog.Find("devstral-small-2-q4")!.Sampling.Temperature);
    }

    [Fact]
    public void ToolCallingGrades()
    {
        Assert.False(ModelCatalog.Find("qwen3.5-4b-q4")!.GoodToolCalling);
        Assert.False(ModelCatalog.Find("qwen3.5-2b-q8")!.GoodToolCalling);
        Assert.All(ModelCatalog.All.Where(m => m.Id is not ("qwen3.5-4b-q4" or "qwen3.5-2b-q8")), m => Assert.True(m.GoodToolCalling, m.Id));
    }

    [Fact]
    public void DefaultFiles_AvoidIqTensorsWherePossible()
    {
        // IQ-квантизация по умолчанию допустима только там, где она и есть смысл записи.
        foreach (var m in ModelCatalog.All)
        {
            if (m.Id == "qwen3.8-27b-iq3") Assert.True(m.DefaultFile!.IqTensors);
            else Assert.False(m.DefaultFile!.IqTensors, m.Id);
        }
        Assert.Contains(ModelCatalog.All, m => m.CpuFriendly && m.ApproxSizeBytes < 2_100_000_000L);
    }

    [Fact]
    public void Parse_RejectsBrokenCatalogs()
    {
        const string ok = """
            { "models": [ { "id": "a", "displayName": "A", "description": "d", "repo": "o/r", "quants": ["Q4_K_M"],
              "files": [ { "quant": "Q4_K_M", "path": "a-Q4_K_M.gguf", "size": 10, "sha256": "0000000000000000000000000000000000000000000000000000000000000000" } ],
              "approxSizeBytes": 10, "paramsB": 1, "activeParamsB": 1, "isMoe": false, "nativeContext": 4096, "defaultContext": 4096,
              "kv": { "layers": 1, "kvHeads": 1, "headDim": 64 }, "goodToolCalling": false,
              "sampling": { "temperature": 0.5 }, "license": "MIT", "minVramGb": 1, "reasoning": "enableThinkingKwarg" } ] }
            """;
        var parsed = ModelCatalog.Parse(ok);
        Assert.Single(parsed);
        Assert.Equal(ReasoningControl.EnableThinkingKwarg, parsed[0].Reasoning);
        Assert.Equal(100, parsed[0].Priority);

        Assert.Throws<InvalidDataException>(() => ModelCatalog.Parse(ok[..40]));
        var dup = ok.Replace("\"models\": [ ", "\"models\": [ " + Extract(ok) + ", ");
        Assert.Throws<InvalidDataException>(() => ModelCatalog.Parse(dup));
        Assert.Throws<InvalidDataException>(() => ModelCatalog.Parse(ok.Replace("\"size\": 10", "\"size\": 0")));
        Assert.Throws<InvalidDataException>(() => ModelCatalog.Parse(ok.Replace("0000000000000000000000000000000000000000000000000000000000000000", "xyz")));
        Assert.Throws<InvalidDataException>(() => ModelCatalog.Parse(ok.Replace("\"approxSizeBytes\": 10", "\"approxSizeBytes\": 11")));
        Assert.Throws<InvalidDataException>(() => ModelCatalog.Parse(ok.Replace("\"isMoe\": false", "\"isMoe\": true")));
        Assert.Throws<InvalidDataException>(() => ModelCatalog.Parse(ok.Replace("a-Q4_K_M.gguf", "../a.gguf")));
        Assert.Throws<InvalidDataException>(() => ModelCatalog.Parse("""{ "models": [] }"""));
    }

    private static string Extract(string json)
    {
        var start = json.IndexOf("{ \"id\"", StringComparison.Ordinal);
        var end = json.LastIndexOf("] }", StringComparison.Ordinal);
        return json[start..end].Trim();
    }
}
