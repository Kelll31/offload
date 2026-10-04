namespace Offload.Models.Tests;

public class HfQueryTests
{
    [Theory]
    [InlineData("Qwen/Qwen3-Coder-30B-A3B-Instruct-GGUF", "Qwen/Qwen3-Coder-30B-A3B-Instruct-GGUF", null)]
    [InlineData("  unsloth/gemma-3-4b-it-GGUF  ", "unsloth/gemma-3-4b-it-GGUF", null)]
    [InlineData("https://huggingface.co/bartowski/Llama-3.2-3B-Instruct-GGUF", "bartowski/Llama-3.2-3B-Instruct-GGUF", null)]
    [InlineData("huggingface.co/bartowski/Llama-3.2-3B-Instruct-GGUF/tree/main", "bartowski/Llama-3.2-3B-Instruct-GGUF", null)]
    [InlineData("hf.co/bartowski/Llama-3.2-3B-Instruct-GGUF:Q4_K_M", "bartowski/Llama-3.2-3B-Instruct-GGUF", "Q4_K_M")]
    [InlineData("https://huggingface.co/o/n/resolve/main/model-Q5_K_S.gguf?download=true", "o/n", "Q5_K_S")]
    [InlineData("https://huggingface.co/o/n/blob/main/model-UD-Q4_K_XL.gguf", "o/n", "UD-Q4_K_XL")]
    [InlineData("huggingface-cli download o/n model-Q8_0.gguf --local-dir x", "o/n", "Q8_0")]
    [InlineData("hf download o/n", "o/n", null)]
    public void ParseReference_Recognizes(string input, string repo, string? quant)
    {
        var r = HfQuery.ParseReference(input);
        Assert.NotNull(r);
        Assert.Equal(repo, r.Repo);
        Assert.Equal(quant, r.Quant);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("qwen coder")]
    [InlineData("qwen3")]
    [InlineData("a/b/c")]
    [InlineData("https://example.com/o/n")]
    [InlineData("C:\\models\\x")]
    [InlineData("https://huggingface.co/datasets/org/name")]
    [InlineData("https://huggingface.co/spaces/org/app")]
    [InlineData("https://huggingface.co/docs/hub")]
    public void ParseReference_IgnoresPlainWords(string? input) => Assert.Null(HfQuery.ParseReference(input));

    [Fact]
    public void Tokens_IgnoreSeparatorsCaseAndDuplicates() =>
        Assert.Equal(["qwen3", "coder", "30b"], HfQuery.Tokens(" Qwen3-Coder  30B qwen3 "));

    [Theory]
    [InlineData("Qwen/Qwen3-Coder-30B-A3B-Instruct-GGUF", "qwen coder", true)]
    [InlineData("Qwen/Qwen3-Coder-30B-A3B-Instruct-GGUF", "coder qwen3 30b", true)]
    [InlineData("unsloth/Qwen3.5-35B-A3B-GGUF", "qwen 3.5", true)]
    [InlineData("unsloth/gemma-3-4b-it-GGUF", "qwen coder", false)]
    public void MatchesAll_AnyOrder(string repo, string query, bool expected) =>
        Assert.Equal(expected, HfQuery.MatchesAll(repo, HfQuery.Tokens(query)));

    [Fact]
    public void HubQuery_TakesLongestToken() =>
        Assert.Equal("coder", HfQuery.HubQuery(HfQuery.Tokens("qwen coder 7b")));

    [Fact]
    public void HubQuery_EmptyForNoTokens() => Assert.Equal("", HfQuery.HubQuery(HfQuery.Tokens("  - ")));
}
