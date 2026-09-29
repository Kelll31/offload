using System.Text.Json;

namespace Offload.Integrations.Tests;

/// <summary>Подключение сервера автодополнения к IDE: файл-блок Continue и фрагмент настроек llama.vscode (в песочнице).</summary>
public sealed class AutocompleteSetupTests
{
    private static readonly FimEndpoint Endpoint = new("http://127.0.0.1:8012", "pc-key'1", "qwen2.5-coder-1.5b-fim-q8");

    [Fact]
    public void ContinueYaml_ModelsBlockWithAutocompleteRole()
    {
        var yaml = AutocompleteSetup.BuildContinueYaml(Endpoint);
        Assert.StartsWith("# x-offload: managed", yaml, StringComparison.Ordinal);
        Assert.Contains("schema: v1\n", yaml);
        Assert.Contains("models:\n", yaml);
        Assert.Contains("    provider: llama.cpp\n", yaml);
        Assert.Contains("    model: 'qwen2.5-coder-1.5b-fim-q8'\n", yaml);
        Assert.Contains("    apiBase: 'http://127.0.0.1:8012/'\n", yaml);
        Assert.Contains("    apiKey: 'pc-key''1'\n", yaml); // одинарная кавычка в YAML удваивается
        Assert.Contains("    roles:\n      - autocomplete\n", yaml);
        Assert.DoesNotContain("mcpServers", yaml);
    }

    [Fact]
    public void Continue_NotInstalled_NothingWritten()
    {
        using var sb = new Sandbox();
        Assert.False(AutocompleteSetup.ContinueInstalled());
        Assert.Equal(AutocompleteTargetState.ClientNotFound, AutocompleteSetup.ContinueStatus(Endpoint).State);
        Assert.False(AutocompleteSetup.ApplyContinue(Endpoint).Ok);
        Assert.False(File.Exists(AutocompleteSetup.ContinueBlockPath));
    }

    [Fact]
    public void Continue_Apply_Idempotent_Update_Remove()
    {
        using var sb = new Sandbox();
        sb.Dir(".continue");
        var path = AutocompleteSetup.ContinueBlockPath;
        Assert.Equal(sb.P(".continue", "models", "offload-autocomplete.yaml"), path);
        Assert.Equal(AutocompleteTargetState.NotConfigured, AutocompleteSetup.ContinueStatus(Endpoint).State);

        var first = AutocompleteSetup.ApplyContinue(Endpoint);
        Assert.True(first.Ok, first.Message);
        Assert.Equal(AutocompleteSetup.BuildContinueYaml(Endpoint), File.ReadAllText(path));
        Assert.Equal(AutocompleteTargetState.Configured, AutocompleteSetup.ContinueStatus(Endpoint).State);
        Assert.Equal(AutocompleteTargetState.Configured, AutocompleteSetup.ContinueStatus(null).State);

        var again = AutocompleteSetup.ApplyContinue(Endpoint);
        Assert.True(again.Ok);
        Assert.Null(again.BackupPath); // без изменений файл не переписывается

        // Порт сменился (8012 был занят) — файл устарел и переписывается с резервной копией.
        var moved = Endpoint with { BaseUrl = "http://127.0.0.1:8013" };
        Assert.Equal(AutocompleteTargetState.Outdated, AutocompleteSetup.ContinueStatus(moved).State);
        var updated = AutocompleteSetup.ApplyContinue(moved);
        Assert.True(updated.Ok);
        Assert.NotNull(updated.BackupPath);
        Assert.Contains("apiBase: 'http://127.0.0.1:8013/'", File.ReadAllText(path));

        // Наш MCP-блок Continue не затрагивается.
        var mcp = sb.Write(sb.P(".continue", "mcpServers", "offload.yaml"), "# x-offload: managed\nname: Offload\n");

        var removed = AutocompleteSetup.RemoveContinue();
        Assert.True(removed.Ok, removed.Message);
        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(sb.P(".continue", "models"))); // пустая папка убрана
        Assert.True(File.Exists(mcp));
        Assert.True(AutocompleteSetup.RemoveContinue().Ok); // повторное удаление — не ошибка
        Assert.Equal(AutocompleteTargetState.NotConfigured, AutocompleteSetup.ContinueStatus(Endpoint).State);
    }

    [Fact]
    public void Continue_UserFileWithoutMarker_NeverTouched()
    {
        using var sb = new Sandbox();
        const string mine = "name: Mine\nversion: 1.0.0\nschema: v1\nmodels:\n  - name: my\n    provider: ollama\n    model: qwen\n";
        var path = sb.Write(sb.P(".continue", "models", "offload-autocomplete.yaml"), mine);
        var other = sb.Write(sb.P(".continue", "models", "my-models.yaml"), mine);

        Assert.Equal(AutocompleteTargetState.Foreign, AutocompleteSetup.ContinueStatus(Endpoint).State);
        Assert.False(AutocompleteSetup.ApplyContinue(Endpoint).Ok);
        Assert.False(AutocompleteSetup.RemoveContinue().Ok);
        Assert.Equal(mine, File.ReadAllText(path));
        Assert.Equal(mine, File.ReadAllText(other));
    }

    [Fact]
    public void Continue_Utf16File_ErrorAndUntouched()
    {
        using var sb = new Sandbox();
        var path = sb.P(".continue", "models", "offload-autocomplete.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = System.Text.Encoding.Unicode.GetPreamble().Concat(System.Text.Encoding.Unicode.GetBytes("# x-offload: managed\n")).ToArray();
        File.WriteAllBytes(path, bytes);

        Assert.Equal(AutocompleteTargetState.Error, AutocompleteSetup.ContinueStatus(Endpoint).State);
        Assert.False(AutocompleteSetup.ApplyContinue(Endpoint).Ok);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void LlamaVscode_DetectedByExtensionFolder_SnippetIsSettingsJsonFragment()
    {
        using var sb = new Sandbox();
        Assert.Equal(AutocompleteTargetState.ClientNotFound, AutocompleteSetup.LlamaVscodeStatus().State);
        sb.Dir(".vscode", "extensions", "ggml-org.llama-vscode-0.0.40");
        Assert.True(AutocompleteSetup.LlamaVscodeInstalled());
        Assert.Equal(AutocompleteTargetState.Manual, AutocompleteSetup.LlamaVscodeStatus().State);

        var snippet = AutocompleteSetup.LlamaVscodeSnippet(Endpoint with { BaseUrl = "http://127.0.0.1:8012/" });
        using var doc = JsonDocument.Parse("{" + snippet + "}");
        Assert.Equal("http://127.0.0.1:8012", doc.RootElement.GetProperty("llama-vscode.endpoint").GetString());
        Assert.Equal("pc-key'1", doc.RootElement.GetProperty("llama-vscode.api_key").GetString());
        Assert.DoesNotContain("{", snippet, StringComparison.Ordinal);
    }
}
