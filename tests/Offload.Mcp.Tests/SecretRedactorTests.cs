using Offload.Core.Config;
using Offload.Mcp.Infrastructure;

namespace Offload.Mcp.Tests;

/// <summary>
/// Маскирование секретов по содержимому (Mcp.RedactSecrets): реалистичные образцы (строки подключения, ключи AWS, токены
/// GitHub/npm, JWT, блоки приватных ключей, «ApiKey» в JSON) и отрицательные примеры кода, который маскировать нельзя.
/// Токены собраны из частей, чтобы сканеры секретов не принимали тесты за утечку.
/// </summary>
[Collection("AppPaths")]
public class SecretRedactorTests
{
    private static readonly string GitHubToken = "gh" + "p_" + "R8mK2qW9zL4vN7bX1cY6hJ3dF5gT0pAsQeUo";
    private static readonly string AwsKey = "AK" + "IA" + "Z7Q4RLWX3B2NPYCD";
    private static readonly string NpmToken = "np" + "m_" + "a1B2c3D4e5F6g7H8i9J0k1L2m3N4o5P6q7R8";
    private static readonly string Jwt = "ey" + "JhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U";
    private const string PemBody1 = "MIIEvQIBADANBgkqhkiG9w0BAQEFAASCBKcwggSjAgEAAoIBAQC7VJTUt9Us8cKj";
    private const string PemBody2 = "MkIRjM2GvIYM1fVxVhJb4OWqFqQ5DWgn2xZ3r9Y8PkT1l2QvW4c2XWh1nWkVvQ==";

    private static int Lines(string s) => s.Split('\n').Length;

    [Fact]
    public void Redact_ConnectionStringInAppSettings_KeepsKeysHidesPassword()
    {
        var json = "{\n  \"ConnectionStrings\": {\n    \"Default\": \"Server=db.local;Database=shop;User Id=sa;Password=S3cr3t!Pass;\"\n  }\n}\n";

        var r = SecretRedactor.Redact(json, "appsettings.json", out var n);

        Assert.DoesNotContain("S3cr3t!Pass", r);
        Assert.Contains("Server=db.local;Database=shop;User Id=sa;Password=«redacted:conn-string-password»;", r);
        Assert.Equal(1, n);
        Assert.Equal(Lines(json), Lines(r));
    }

    [Fact]
    public void Redact_ApiKeyInJson_KeepsKey()
    {
        var json = "{ \"Payments\": { \"ApiKey\": \"q8Hk2mZ7vP4wR9tY\", \"ClientSecret\": \"x9!Lm2#pQ7\", \"Timeout\": \"00:00:30\" } }";

        var r = SecretRedactor.Redact(json);

        Assert.Contains("\"ApiKey\": \"«redacted:hardcoded-secret»\"", r);
        Assert.Contains("\"ClientSecret\": \"«redacted:hardcoded-secret»\"", r);
        Assert.Contains("\"Timeout\": \"00:00:30\"", r);
        Assert.DoesNotContain("q8Hk2mZ7vP4wR9tY", r);
    }

    [Fact]
    public void Redact_KnownTokenFormats_AreReplacedWhole()
    {
        var text = $"aws = {AwsKey}\ngh: {GitHubToken}\nAuthorization: Bearer {Jwt}\n";

        var r = SecretRedactor.Redact(text, "notes.txt", out var n);

        Assert.DoesNotContain(AwsKey, r);
        Assert.DoesNotContain(GitHubToken, r);
        Assert.DoesNotContain(Jwt, r);
        Assert.Contains("«redacted:aws-key»", r);
        Assert.Contains("«redacted:github-token»", r);
        Assert.Contains("«redacted:jwt»", r);
        Assert.Equal(3, n);
    }

    [Fact]
    public void Redact_NpmrcToken_Hidden()
    {
        var npmrc = $"registry=https://registry.npmjs.org/\n//registry.npmjs.org/:_authToken={NpmToken}\nalways-auth=true\n";

        var r = SecretRedactor.Redact(npmrc, ".npmrc");

        Assert.DoesNotContain(NpmToken, r);
        Assert.Contains("//registry.npmjs.org/:_authToken=«redacted:", r);
        Assert.Contains("registry=https://registry.npmjs.org/", r);
    }

    [Fact]
    public void Redact_UrlCredentials_HidesOnlyPassword()
    {
        var r = SecretRedactor.Redact("DATABASE_URL=postgres://app:Pa55w0rd-x@db.internal:5432/app");

        Assert.Equal("DATABASE_URL=postgres://app:«redacted:url-credentials»@db.internal:5432/app", r);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Redact_PrivateKeyBlock_BodyHiddenLinesPreserved(string nl)
    {
        var text = string.Join(nl, "// ключ для тестового стенда", "-----BEGIN RSA PRIVATE KEY-----", PemBody1, PemBody2,
            "-----END RSA PRIVATE KEY-----", "var x = 1;", "");

        var r = SecretRedactor.Redact(text, "deploy/key.txt", out var n);

        Assert.DoesNotContain(PemBody1, r);
        Assert.DoesNotContain(PemBody2, r);
        Assert.Contains("-----BEGIN RSA PRIVATE KEY-----" + nl + "«redacted:private-key»" + nl + nl + "-----END RSA PRIVATE KEY-----" + nl + "var x = 1;", r);
        Assert.Equal(1, n);
        Assert.Equal(Lines(text), Lines(r));
    }

    [Fact]
    public void Redact_EscapedPrivateKeyInServiceAccountJson_Hidden()
    {
        var json = "{\"type\": \"service_account\", \"private_key\": \"-----BEGIN PRIVATE KEY-----\\n" + PemBody1 + "\\n" + PemBody2 +
                   "\\n-----END PRIVATE KEY-----\\n\", \"client_email\": \"svc@proj.iam.gserviceaccount.com\"}";

        var r = SecretRedactor.Redact(json, "sa.json");

        Assert.DoesNotContain(PemBody1, r);
        Assert.Contains("-----BEGIN PRIVATE KEY-----\\n«redacted:private-key»\\n-----END PRIVATE KEY-----", r);
        Assert.Contains("svc@proj.iam.gserviceaccount.com", r);
    }

    [Fact]
    public void Redact_UnquotedConfigValues_OnlyInConfigFiles()
    {
        var yaml = "spring:\n  datasource:\n    username: app\n    password: hunter22x # prod\n    url: ${DB_URL}\napi_key: ${API_KEY}\n";

        var r = SecretRedactor.Redact(yaml, "src/main/resources/application.yml");

        Assert.Contains("password: «redacted:config-secret» # prod", r);
        Assert.Contains("username: app", r);
        Assert.Contains("api_key: ${API_KEY}", r);
        // Тот же текст в коде (не конфиг) без кавычек не трогаем: «password: string» в TypeScript — тип, а не значение.
        Assert.Equal("password: string;", SecretRedactor.Redact("password: string;", "src/models/user.ts"));
    }

    [Fact]
    public void Redact_XmlConfigs_Hidden()
    {
        var xml = "<appSettings>\n  <add key=\"StripeApiKey\" value=\"k7Qw9Zp2Lm4X\" />\n  <add key=\"Theme\" value=\"dark-blue\" />\n</appSettings>\n" +
                  "<server><id>corp</id><password>m4v3nS3cret</password></server>";

        var r = SecretRedactor.Redact(xml, "web.config");

        Assert.Contains("key=\"StripeApiKey\" value=\"«redacted:hardcoded-secret»\"", r);
        Assert.Contains("value=\"dark-blue\"", r);
        Assert.Contains("<password>«redacted:hardcoded-secret»</password>", r);
        Assert.Contains("<id>corp</id>", r);
    }

    /// <summary>Код, в котором «password/secret/token» — имена, типы, сравнения и шаблоны, а не значения.</summary>
    [Theory]
    [InlineData("public string Password { get; set; }")]
    [InlineData("if (password == \"\") return false;")]
    [InlineData("if (options.Password == \"hunter2x\") Fail();")]
    [InlineData("if (user == null || password == \"\") return;")]
    [InlineData("var password = GetPassword();")]
    [InlineData("connection.Password = password;")]
    [InlineData("string password = Console.ReadLine();")]
    [InlineData("[JsonPropertyName(\"password\")] public string Pwd { get; init; } = \"\";")]
    [InlineData("public bool ValidatePassword(string password, int minLength = 8)")]
    [InlineData("def login(user, password):")]
    [InlineData("password = request.form[\"password\"]")]
    [InlineData("parser.add_argument(\"--password\", help=\"Database password\")")]
    [InlineData("<input type=\"password\" name=\"password\" autocomplete=\"current-password\" />")]
    [InlineData("const apiKey = process.env.API_KEY;")]
    [InlineData("password: z.string().min(8),")]
    [InlineData("\"Password\": \"{password}\",")]
    [InlineData("\"password\": \"changeme\",")]
    [InlineData("\"password\": \"Пароль\",")]
    [InlineData("\"passwordHint\": \"at-least-8-chars\",")]
    [InlineData("\"secretName\": \"prod-db-credentials\",")]
    [InlineData("\"token\": \"Bearer\",")]
    [InlineData("\"key\": \"welcome_message\",")]
    [InlineData("apiKey: \"${API_KEY}\",")]
    [InlineData("\"password\": \"DB_PASSWORD\",")]
    [InlineData("\"ExampleKey\": \"AKIAIOSFODNN7EXAMPLE\"")]
    [InlineData("const string Header = \"-----BEGIN PRIVATE KEY-----\";")]
    [InlineData("password = GetPwd(); // Server=localhost")]
    [InlineData("git clone git@github.com:org/repo.git")]
    [InlineData("see https://example.org:8443/path@v2")]
    public void Redact_CodeWithoutSecrets_Unchanged(string code)
    {
        Assert.Equal(code, SecretRedactor.Redact(code, "src/app.cs"));
    }

    [Fact]
    public void Redact_LiteralPemHeaderFollowedByCode_Unchanged()
    {
        var code = "if (text.StartsWith(\"-----BEGIN PRIVATE KEY-----\"))\n{\n    return Parse(text);\n}\nelse\n    return null;\n";

        Assert.Equal(code, SecretRedactor.Redact(code, "src/Pem.cs"));
    }

    [Fact]
    public void Redact_HardcodedPasswordInCode_Hidden()
    {
        var code = "var client = new SmtpClient(\"smtp.local\") { Credentials = new(\"bot\", \"x\") };\noptions.Password = \"Tr0ub4dor&3\";\n";

        var r = SecretRedactor.Redact(code, "src/Mailer.cs");

        Assert.Contains("options.Password = \"«redacted:hardcoded-secret»\";", r);
        Assert.Equal(Lines(code), Lines(r));
    }

    [Fact]
    public async Task Gather_AppSettings_RedactsBeforeLocalModel()
    {
        using var env = new TestEnv();
        env.WriteFile("appsettings.json", "{ \"ConnectionStrings\": { \"Db\": \"Host=pg;Username=app;Password=Zx9!pQ2w;\" }, \"Jwt\": { \"SigningKey\": \"f3Kq9Lm2Zx8Vb7Nc\" } }\n");
        var opts = new GatherOptions(512 * 1024, 4 * 1024 * 1024, new McpSettings().SecretFilePatterns);

        var r = await FileGatherer.GatherAsync(["appsettings.json"], [env.Workspace], opts, TestContext.Current.CancellationToken);

        var text = Assert.Single(r.Files).Text;
        Assert.DoesNotContain("Zx9!pQ2w", text);
        Assert.DoesNotContain("f3Kq9Lm2Zx8Vb7Nc", text);
        Assert.Contains("Password=«redacted:conn-string-password»", text);
        Assert.Equal(2, r.RedactedSecrets);
        Assert.Contains("2 secret values redacted", r.CoverageLine());

        // Mcp.RedactSecrets = false — текст как в файле.
        var raw = await FileGatherer.GatherAsync(["appsettings.json"], [env.Workspace], opts with { RedactSecrets = false }, TestContext.Current.CancellationToken);
        Assert.Contains("Zx9!pQ2w", Assert.Single(raw.Files).Text);
        Assert.Equal("coverage: full (1 file)", raw.CoverageLine());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RedactingOutput_FollowsSetting(bool enabled)
    {
        using var env = new TestEnv(configure: c => c.Mcp.RedactSecrets = enabled);
        var snippet = $"src/Config.cs:12:    const string Token = \"{GitHubToken}\";";

        var result = await SecretRedactor.RedactingOutput(_ => Task.FromResult(snippet))(env.Context());

        Assert.Equal(!enabled, result.Contains(GitHubToken, StringComparison.Ordinal));
        if (enabled) Assert.Contains("src/Config.cs:12:    const string Token = \"«redacted:", result);
    }

    [Fact]
    public void CodeScanSecrets_StillDetectsAndMasks()
    {
        var hits = CodeRules.Secrets($"token = \"{GitHubToken}\"").ToList();

        Assert.Contains(hits, h => h.Rule == "github-token");
        Assert.All(hits, h => Assert.DoesNotContain(GitHubToken, h.Message));
        Assert.Contains(CodeRules.Secrets("//registry.npmjs.org/:_authToken=" + NpmToken), h => h.Rule == "npm-token");
    }
}
