using FreeSql;
using FreeSql.Internal;
using Agent_QC.Services;

var builder = WebApplication.CreateBuilder(args);

// ── 数据库配置 ──────────────────────────────────────
var dbConfig = builder.Configuration.GetSection("Database");
var dbType = (dbConfig["Type"] ?? "SQLite").ToUpperInvariant();

var dataType = dbType switch
{
    "POSTGRESQL" => DataType.PostgreSQL,
    "ORACLE" => DataType.Oracle,
    _ => DataType.Sqlite
};

var connStr = Environment.GetEnvironmentVariable("DB_CONNECTION_STRING")
    ?? dbConfig["ConnectionString"]
    ?? "Data Source=Data/qc.db";

if (dataType == DataType.Sqlite)
{
    var dataDir = Path.Combine(AppContext.BaseDirectory, "Data");
    Directory.CreateDirectory(dataDir);
    connStr = $"Data Source={Path.Combine(dataDir, "qc.db")}";
}

// ── 主数据库 (SQLite) ──────────────────────────────
var freeSql = new FreeSqlBuilder()
    .UseConnectionString(dataType, connStr)
    .UseNameConvert(NameConvertType.PascalCaseToUnderscore)
    .UseAutoSyncStructure(true)
    .Build();
builder.Services.AddSingleton(freeSql);

// ── Oracle 连接 ────────────────────────────────────
// ⚠ Oracle.ManagedDataAccess on Linux is sensitive to http_proxy/https_proxy env vars
// Clear them before creating the Oracle connection to avoid ORA-50201
var oracleFreeSql = default(IFreeSql);
var oracleConnStr = dbConfig["OracleConnectionString"];
if (!string.IsNullOrWhiteSpace(oracleConnStr))
{
    try
    {
        var savedHttpProxy = Environment.GetEnvironmentVariable("http_proxy");
        var savedHttpsProxy = Environment.GetEnvironmentVariable("https_proxy");
        Environment.SetEnvironmentVariable("http_proxy", "");
        Environment.SetEnvironmentVariable("https_proxy", "");

        oracleFreeSql = new FreeSqlBuilder()
            .UseConnectionString(DataType.Oracle, oracleConnStr)
            .UseNameConvert(NameConvertType.PascalCaseToUnderscoreWithUpper)
            .UseAutoSyncStructure(false)
            .Build();

        Environment.SetEnvironmentVariable("http_proxy", savedHttpProxy);
        Environment.SetEnvironmentVariable("https_proxy", savedHttpsProxy);

        Console.WriteLine("[Oracle] FreeSql instance created");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[Oracle] 初始化失败: {ex.Message}");
    }
}
if (oracleFreeSql != null)
    builder.Services.AddSingleton(new ReportQueryService(oracleFreeSql));
else
    builder.Services.AddSingleton(new ReportQueryService(freeSql));

// ── jieba 中文分词 ──────────────────────────────────
var dictPath = Path.Combine(AppContext.BaseDirectory, "knowledge", "jieba_medical_dict.txt");
builder.Services.AddSingleton(new JiebaSegmenter(dictPath));

// ── RuleEngine (replaces individual rule classes) ──
var rulesDbPath = Path.Combine(AppContext.BaseDirectory, "knowledge", "rules.db");
var ruleEngine = new RuleEngine(rulesDbPath);
ruleEngine.Initialize();
builder.Services.AddSingleton(ruleEngine);

// ── Level 2: RoBERTa NER + Logic Engine ─────────────
var terminologyPath = Path.Combine(AppContext.BaseDirectory, "knowledge", "terminology.yaml");
var entityNormalizer = new EntityNormalizer(terminologyPath);
builder.Services.AddSingleton(entityNormalizer);

builder.Services.AddSingleton<LogicEngine>();

var modelPath = Path.Combine(AppContext.BaseDirectory, "knowledge", "models", "roberta-ner.onnx");
var vocabPath = Path.Combine(AppContext.BaseDirectory, "knowledge", "models", "vocab.txt");
builder.Services.AddSingleton(sp =>
{
    var jieba = sp.GetRequiredService<JiebaSegmenter>();
    var normalizer = sp.GetRequiredService<EntityNormalizer>();
    var service = new RobertaNerService(jieba, normalizer, modelPath, vocabPath);
    service.Initialize();
    return service;
});

// ── QC 服务 ─────────────────────────────────────────
builder.Services.AddSingleton<IQcService, QcService>();

// ── vLLM + Skill Squad ────────────────────────────
var vllmEndpoint = builder.Configuration["Vllm:Endpoint"] ?? "http://localhost:8100";
var vllmModel = builder.Configuration["Vllm:Model"] ?? "QuantTrio/Qwen3.5-2B-AWQ";
var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(300) };
var vllmClient = new VllmClient(httpClient, vllmEndpoint, vllmModel);
_ = vllmClient.CheckHealthAsync().ContinueWith(t =>
{
    var status = t.GetAwaiter().GetResult() ? "healthy" : "unavailable";
    Console.WriteLine($"[VllmClient] vLLM health check: {status}");
});
builder.Services.AddSingleton<IVllmClient>(vllmClient);
builder.Services.AddSingleton(new SkillRegistry("knowledge/skills"));
builder.Services.AddSingleton<HermesOrchestrator>();
builder.Services.AddSingleton<QaArbiter>();

// ── 控制器 ─────────────────────────────────────────
builder.Services.AddControllers()
    .AddJsonOptions(opts =>
    {
        opts.JsonSerializerOptions.PropertyNamingPolicy =
            System.Text.Json.JsonNamingPolicy.CamelCase;
        opts.JsonSerializerOptions.DefaultIgnoreCondition =
            System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// ── CORS ───────────────────────────────────────────
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
    });
});

var app = builder.Build();


if (app.Configuration.GetValue<bool>("EnableSwagger"))
{
    app.UseSwagger();
    app.UseSwaggerUI();
    // 👇 自动根路径 / 跳转到 swagger
    app.MapGet("/", () => Results.Redirect("/swagger/index.html"));
}


app.UseCors("AllowAll");
app.UseAuthorization();
app.MapControllers();

app.Run();
