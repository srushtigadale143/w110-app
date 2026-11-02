// Stand-in W1.10 foundation app (release foundation-observe-b) - NOT the official course app.
// Routes: /  /client.json  /healthz  /api/boards/{reference|missing|slow|overdue}
// Every /api request writes one JSON completion record to stdout (app.log) and, if LOCAL_LOKI_URL is set, to Loki.
using System.Diagnostics;
using System.Text;
using System.Text.Json;

const string Release = "foundation-observe-b";
var environment = Environment.GetEnvironmentVariable("LAB_ENV") ?? "workshop-blue";
var session = Environment.GetEnvironmentVariable("LAB_SESSION") ?? "local";
var lokiUrl = Environment.GetEnvironmentVariable("LOCAL_LOKI_URL");
var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();   // keep app.log to our completion records only
var app = builder.Build();
var board = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "board-reference.json"));

app.UseDefaultFiles();
app.UseStaticFiles();

// Completion record: written AFTER the handler finishes, even if the caller already gave up.
app.Use(async (ctx, next) =>
{
    if (!ctx.Request.Path.StartsWithSegments("/api")) { await next(); return; }
    var requestId = ctx.Request.Headers["X-Request-Id"].FirstOrDefault() ?? Guid.NewGuid().ToString();
    var actionId = ctx.Request.Headers["X-Action-Id"].FirstOrDefault();
    ctx.Response.Headers["X-Request-Id"] = requestId;
    ctx.Response.Headers["X-Lab-Release"] = Release;
    ctx.Response.Headers["X-Lab-Environment"] = environment;
    var sw = Stopwatch.StartNew();
    try { await next(); } catch (Exception) when (ctx.RequestAborted.IsCancellationRequested) { /* caller left mid-write */ }
    sw.Stop();
    var ms = (int)sw.ElapsedMilliseconds;
    var record = JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["ts"] = DateTimeOffset.UtcNow.ToString("o"), ["job"] = "academy-w110", ["session"] = session,
        ["request_id"] = requestId, ["action_id"] = actionId, ["method"] = ctx.Request.Method,
        ["path"] = ctx.Request.Path.Value, ["status"] = ctx.Response.StatusCode,
        ["result_status"] = ctx.Items["result_status"], ["response_status"] = ctx.Response.StatusCode,
        ["duration_ms"] = ms, ["elapsed_ms"] = ms, ["caller_aborted"] = ctx.RequestAborted.IsCancellationRequested,
        ["release"] = Release, ["environment"] = environment,
    });
    Console.WriteLine(record);
    if (lokiUrl is not null)
    {
        var ns = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000).ToString();
        var push = JsonSerializer.Serialize(new
        {
            streams = new[] { new { stream = new { job = "academy-w110", session }, values = new[] { new[] { ns, record } } } }
        });
        try { await http.PostAsync(lokiUrl + "/loki/api/v1/push", new StringContent(push, Encoding.UTF8, "application/json")); }
        catch (Exception ex) { Console.WriteLine($"WARNING Loki delivery failed: {ex.Message}"); }
    }
});

app.MapGet("/healthz", () => Results.Text("Healthy"));   // narrow liveness only: says nothing about boards

app.MapGet("/api/boards/{id}", async (string id, HttpContext ctx) =>
{
    // Server processing time per fixture. The delay deliberately ignores the caller's cancellation,
    // so the server can still "select" 200 after the browser has already aborted.
    int delay = id switch { "slow" => 300, "overdue" => 900, _ => 0 };
    if (id is not ("reference" or "slow" or "overdue"))
    {
        ctx.Items["result_status"] = 404;
        return Results.Json(new { code = "NOT_FOUND", message = $"board '{id}' does not exist" }, statusCode: 404);
    }
    if (delay > 0) await Task.Delay(delay);
    ctx.Items["result_status"] = 200;
    return Results.Text(board, "application/json");
});

app.Run();
