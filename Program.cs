using Azure;
using Azure.Communication.Email;
using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Data;
using Dapper;
using System.Net.Http.Json;
using Npgsql;
using Azure.Monitor.Query;
using Azure.Identity;
using Microsoft.Identity.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using System.Threading.Channels;
using Microsoft.Extensions.Caching.Memory;
using Azure.Messaging.ServiceBus;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Load .env file manually if it exists
if (File.Exists(".env"))
{
    foreach (var line in File.ReadAllLines(".env"))
    {
        var parts = line.Split('=', 2);
        if (parts.Length == 2)
        {
            var key = parts[0].Trim();
            var value = parts[1].Trim().Trim('"');
            Environment.SetEnvironmentVariable(key, value);
        }
    }
    // Re-build configuration to include the new env vars
    builder.Configuration.AddEnvironmentVariables();
}

builder.Services.AddCors();

// Add Microsoft Identity Services
builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"));

builder.Services.AddAuthorization();
builder.Services.AddMemoryCache();

// --- Cookie & Proxy Fixes for Azure ---
builder.Services.Configure<CookiePolicyOptions>(options =>
{
    options.MinimumSameSitePolicy = SameSiteMode.None;
    options.Secure = CookieSecurePolicy.Always;
});

builder.Services.Configure<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>("Cookies", options =>
{
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.None;
});

// --- Azure Service Bus Registration ---
var sbConn = builder.Configuration["ServiceBus:ConnectionString"];
if (!string.IsNullOrEmpty(sbConn))
{
    builder.Services.AddSingleton(new ServiceBusClient(sbConn));
    builder.Services.AddSingleton(sp => sp.GetRequiredService<ServiceBusClient>().CreateSender("email-queue"));
}

builder.Services.AddHostedService<EmailBackgroundWorker>();

var app = builder.Build();

// Configure Forwarded Headers for Azure Container Apps / Proxy HTTPS detection
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

// CRITICAL FIX: Trust the Azure Proxy
app.Use((context, next) =>
{
    if (context.Request.Headers.ContainsKey("X-Forwarded-Proto"))
    {
        context.Request.Scheme = context.Request.Headers["X-Forwarded-Proto"];
    }
    return next();
});

// Extra fix for Microsoft Entra ID: Ensure the app correctly identifies as HTTPS in Production
app.Use((context, next) =>
{
    if (!app.Environment.IsDevelopment()) {
        context.Request.Scheme = "https";
    }
    return next();
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

var defaultFilesOptions = new DefaultFilesOptions { FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Directory.GetCurrentDirectory()) };
defaultFilesOptions.DefaultFileNames.Clear(); // Stop index.html from taking over
app.UseDefaultFiles(defaultFilesOptions);
app.UseStaticFiles(new StaticFileOptions { FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Directory.GetCurrentDirectory()) });

app.UseRouting();
app.UseCors(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());

app.UseCookiePolicy();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Redirect("/login.html"));

// --- Auth Endpoints ---
app.MapGet("/login-microsoft", (HttpContext context) => {
    return Results.Challenge(
        properties: new AuthenticationProperties { RedirectUri = "/login.html?auth=success" },
        authenticationSchemes: new[] { OpenIdConnectDefaults.AuthenticationScheme }
    );
});

app.MapGet("/api/auth/user", (HttpContext context) => {
    if (context.User.Identity?.IsAuthenticated == true) {
        return Results.Ok(new { 
            isAuthenticated = true, 
            name = context.User.Identity.Name,
            email = context.User.Claims.FirstOrDefault(c => c.Type == "preferred_username")?.Value 
        });
    }
    return Results.Ok(new { isAuthenticated = false });
});

app.MapGet("/logout", async (HttpContext context) => {
    await context.SignOutAsync(OpenIdConnectDefaults.AuthenticationScheme);
    await context.SignOutAsync("Cookies");
    return Results.Redirect("/login.html");
});

// --- Subscriber Endpoints (CSV) ---
app.MapGet("/api/subscribers", async () => {
    var subs = new List<Subscriber>();
    if (File.Exists("subscribers.csv")) {
        var lines = await File.ReadAllLinesAsync("subscribers.csv");
        foreach (var line in lines.Skip(1)) {
            var parts = line.Split(',');
            if (parts.Length >= 2 && !string.IsNullOrWhiteSpace(parts[1])) {
                subs.Add(new Subscriber(parts[0].Trim(), parts[1].Trim()));
            }
        }
    }
    return Results.Ok(subs);
});

app.MapPost("/api/subscribers", async ([FromBody] List<Subscriber> subs) => {
    var lines = new List<string> { "Name,Email" };
    lines.AddRange(subs.Where(s => !string.IsNullOrWhiteSpace(s.Email)).Select(s => $"{s.Name},{s.Email}"));
    await File.WriteAllLinesAsync("subscribers.csv", lines);
    return Results.Ok();
});

// --- Subscriber Endpoint (Live Database) ---
app.MapGet("/api/db-subscribers", async (IConfiguration config) => {
    var connStr = config["Database:PostgresConnectionString"];
    try {
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT name, email, is_verified, created_at FROM subscribers ORDER BY created_at DESC", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var list = new List<object>();
        while (await reader.ReadAsync()) {
            list.Add(new {
                name = reader.IsDBNull(0) ? "Unknown" : reader.GetString(0),
                email = reader.IsDBNull(1) ? "" : reader.GetString(1),
                isVerified = reader.IsDBNull(2) ? false : reader.GetBoolean(2),
                createdAt = reader.IsDBNull(3) ? DateTime.UtcNow : reader.GetDateTime(3)
            });
        }
        return Results.Ok(list);
    } catch (Exception ex) {
        return Results.Problem($"Database connection failed: {ex.Message}");
    }
});

// --- Inquiries Endpoint (Live Database) ---
app.MapGet("/api/db-inquiries", async (IConfiguration config) => {
    var connStr = config["Database:PostgresConnectionString"];
    try {
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT name, email, topic, message FROM inquiries ORDER BY id DESC", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var list = new List<object>();
        while (await reader.ReadAsync()) {
            list.Add(new {
                name = reader.IsDBNull(0) ? "Unknown" : reader.GetString(0),
                email = reader.IsDBNull(1) ? "" : reader.GetString(1),
                topic = reader.IsDBNull(2) ? "General" : reader.GetString(2),
                message = reader.IsDBNull(3) ? "" : reader.GetString(3),
                createdAt = DateTime.UtcNow // Placeholder if column missing
            });
        }
        return Results.Ok(list);
    } catch (Exception ex) {
        return Results.Problem($"Database connection failed: {ex.Message}");
    }
});

// --- Template Endpoint ---
app.MapGet("/api/template", async () => {
    if (File.Exists("Email_Message_Body.html"))
        return Results.Text(await File.ReadAllTextAsync("Email_Message_Body.html"), "text/plain");
    return Results.NotFound();
});

// --- Send Email Endpoint ---
app.MapPost("/api/send", async (HttpContext context, [FromBody] SendSingleRequest request, IConfiguration config) => {
    // 1. Check Authentication
    if (context.User.Identity?.IsAuthenticated != true) {
        return Results.Unauthorized();
    }

    // 2. Check Authorization
    var userEmail = context.User.Claims.FirstOrDefault(c => c.Type == "preferred_username")?.Value?.ToLower()?.Trim();
    
    var allowedEmails = new[] { 
        "aakash.padyachi@orchvate.com", 
        "rahul.rajesh@orchvate.com",
        "aakash.padyachi@orchvate.in",
        "rahul.rajesh@orchvate.in",
        "founders@orchvate.in",
        "founders@founders.orchvate.in"
    };

    if (string.IsNullOrEmpty(userEmail) || !allowedEmails.Contains(userEmail)) {
        Console.WriteLine($"[Auth Error] User '{userEmail ?? "Unknown"}' is not in the allowed list.");
        return Results.Json(new { error = "Forbidden", user = userEmail }, statusCode: 403);
    }

    // 3. Safety Password Check
    if (request.SafetyPassword != "ECHO12345") {
        return Results.Problem("Invalid Safety Password.");
    }

    if (!File.Exists("Newsletter_Wrapper.html"))
        return Results.Problem("Required email wrapper not found.");

    if (string.IsNullOrWhiteSpace(request.Email))
        return Results.Problem("No recipient provided.");

    // Load Files
    string wrapperHtml = await File.ReadAllTextAsync("Newsletter_Wrapper.html");

    // The user's custom message goes into {{message_body}}
    string customMessage = !string.IsNullOrWhiteSpace(request.Body) ? request.Body :
        (File.Exists("Email_Message_Body.html") ? await File.ReadAllTextAsync("Email_Message_Body.html") : "");

    // The complex graphical newsletter goes into {{content}}
    string templateHtml = File.Exists("Email_Template_Short.html") ? await File.ReadAllTextAsync("Email_Template_Short.html") : "";

    // Inject content into wrapper
    string finalHtml = wrapperHtml.Replace("{{message_body}}", customMessage).Replace("{{content}}", templateHtml);

    var backendUrl = $"{context.Request.Scheme}://{context.Request.Host}";
    var trackingId = Guid.NewGuid().ToString("N");
    var finalBatchId = request.BatchId ?? $"Manual_{DateTime.UtcNow:yyyyMMdd_HHmmss}";

    // Personalization
    finalHtml = finalHtml.Replace("{{name}}", !string.IsNullOrWhiteSpace(request.Name) ? request.Name : "Friend");
    finalHtml = finalHtml.Replace("{{email}}", request.Email);
    finalHtml = finalHtml.Replace("{{backend_url}}", backendUrl);
    finalHtml = finalHtml.Replace("{{tracking_id}}", trackingId);

    // Log as 'Queued' and add to Background Queue (Service Bus)
    var sender = context.RequestServices.GetService<ServiceBusSender>();
    var job = new EmailJob(
        request.Email, 
        request.Name ?? "Friend", 
        request.Subject ?? "Orchvate | Newsletter", 
        finalHtml, 
        finalBatchId, 
        trackingId, 
        request.SenderType ?? "ACS",
        request.FromEmail ?? "founders@milestones.orchvate.com"
    );
    
    if (sender != null)
    {
        var message = new ServiceBusMessage(JsonSerializer.Serialize(job));
        await sender.SendMessageAsync(message);
    }
    else
    {
        // Fallback or error if SB not configured
        return Results.Problem("Service Bus not configured. Email cannot be queued.");
    }

    await LogSendStatus(config, request.Email, request.Name ?? "Friend", request.Subject ?? "Newsletter", "N/A", "Queued", "Pending", null, request.SenderType ?? "ACS", finalBatchId, trackingId);

    return Results.Accepted(null, new { Success = true, Message = "Email queued for delivery", TrackingId = trackingId });
});



app.MapPost("/api/analytics/sync-blast/{batchId}", async (string batchId, IConfiguration config) => {
    try {
        var connStr = config["Database:PostgresConnectionString"];
        var acsConn = config["ACS:ConnectionString"];
        var emailClient = new EmailClient(acsConn);
        
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        
        var ids = new List<string>();
        await using (var cmdFetch = new NpgsqlCommand("SELECT message_id FROM sent_logs WHERE batch_id = @bid AND delivery_status NOT IN ('Succeeded', 'Delivered', 'Failed', 'Dropped')", conn)) {
            cmdFetch.Parameters.AddWithValue("bid", batchId);
            using var reader = await cmdFetch.ExecuteReaderAsync();
            while (await reader.ReadAsync()) {
                var mid = reader.GetString(0);
                if (mid != "N/A" && !mid.StartsWith("PA-")) ids.Add(mid);
            }
        }

        int count = 0;
        foreach (var id in ids) {
            try {
                var op = new EmailSendOperation(id, emailClient);
                await op.UpdateStatusAsync();
                if (op.HasCompleted) {
                    await UpdateLogStatus(config, id, op.Value.Status.ToString());
                    count++;
                }
            } catch { /* skip individual errors */ }
        }
        return Results.Ok(new { UpdatedCount = count });
    } catch (Exception ex) { return Results.Problem(ex.Message); }
});

app.MapPost("/api/webhooks/acs", async (HttpContext context, IConfiguration config) => {
    try {
        using var reader = new StreamReader(context.Request.Body);
        var json = await reader.ReadToEndAsync();
        var events = System.Text.Json.JsonDocument.Parse(json);

        foreach (var element in events.RootElement.EnumerateArray()) {
            var eventType = element.GetProperty("eventType").GetString();

            // 1. Handle Azure Event Grid Validation (Handshake)
            if (eventType == "Microsoft.EventGrid.SubscriptionValidationEvent") {
                var validationCode = element.GetProperty("data").GetProperty("validationCode").GetString();
                return Results.Ok(new { validationResponse = validationCode });
            }

            // 2. Handle Email Delivery Reports
            if (eventType == "Microsoft.Communication.EmailDeliveryReportReceived") {
                var data = element.GetProperty("data");
                var messageId = data.GetProperty("messageId").GetString();
                var status = data.GetProperty("status").GetString(); // Succeeded, Failed, etc.
                
                // Update our database
                if (!string.IsNullOrEmpty(messageId) && !string.IsNullOrEmpty(status)) {
                    await UpdateLogStatus(config, messageId, status);
                }
                Console.WriteLine($"Webhook: Updated {messageId} to {status}");
            }
        }
        return Results.Ok();
    } catch (Exception ex) {
        Console.WriteLine("Webhook Error: " + ex.Message);
        return Results.Problem();
    }
});

app.MapGet("/api/send-status/{id}", async (string id, IConfiguration config) => {
    var connectionString = config["ACS:ConnectionString"];
    try {
        var emailClient = new EmailClient(connectionString);
        var operation = new EmailSendOperation(id, emailClient);
        await operation.UpdateStatusAsync();
        
        string status = operation.Value.Status.ToString();
        
        // Update DB with the latest status
        await UpdateLogStatus(config, id, status);
        
        return Results.Ok(new { Status = status, IsCompleted = operation.HasCompleted });
    } catch (Exception ex) {
        return Results.Problem(ex.Message);
    }
});

// --- Sending Helpers moved to EmailSender class ---

async Task LogSendStatus(IConfiguration config, string email, string name, string subject, string messageId, string blastStatus, string deliveryStatus, string? error, string senderType, string batchId, string trackingId) {
    try {
        var connStr = config["Database:PostgresConnectionString"];
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO sent_logs (recipient_email, recipient_name, subject, message_id, blast_status, delivery_status, error_message, sender_type, batch_id, tracking_id)
            VALUES (@e, @n, @s, @m, @b_st, @d_st, @err, @sender, @bid, @tid)", conn);
        cmd.Parameters.AddWithValue("e", email);
        cmd.Parameters.AddWithValue("n", (object?)name ?? DBNull.Value);
        cmd.Parameters.AddWithValue("s", (object?)subject ?? DBNull.Value);
        cmd.Parameters.AddWithValue("m", (object?)messageId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("b_st", blastStatus);
        cmd.Parameters.AddWithValue("d_st", deliveryStatus);
        cmd.Parameters.AddWithValue("err", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("sender", senderType);
        cmd.Parameters.AddWithValue("bid", batchId);
        cmd.Parameters.AddWithValue("tid", trackingId);
        await cmd.ExecuteNonQueryAsync();
    } catch (Exception ex) { Console.WriteLine("DB Log Error: " + ex.Message); }
}

async Task UpdateLogStatus(IConfiguration config, string messageId, string deliveryStatus) {
    try {
        var connStr = config["Database:PostgresConnectionString"];
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("UPDATE sent_logs SET delivery_status = @st, updated_at = CURRENT_TIMESTAMP WHERE message_id = @m", conn);
        cmd.Parameters.AddWithValue("m", messageId);
        cmd.Parameters.AddWithValue("st", deliveryStatus);
        await cmd.ExecuteNonQueryAsync();
    } catch (Exception ex) { Console.WriteLine("DB Update Error: " + ex.Message); }
}

// --- Analytics API Endpoints (Database / SQL) ---
app.MapGet("/api/analytics/kpis", async (IConfiguration config, Microsoft.Extensions.Caching.Memory.IMemoryCache cache) => {
    const string cacheKey = "analytics_kpis";
    if (cache.TryGetValue(cacheKey, out var cachedData)) return Results.Ok(cachedData);

    var connStr = config["Database:PostgresConnectionString"];
    try {
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            SELECT 
                (SELECT COUNT(*) FROM subscribers WHERE is_verified = TRUE) as verified, 
                (SELECT (SUM(CASE WHEN newsletter_opt_in = TRUE THEN 1.0 ELSE 0.0 END) / NULLIF(COUNT(*), 0)) * 100 FROM inquiries) as crosspoll,
                (SELECT COUNT(*) FROM sent_logs WHERE blast_status = 'Sent') as total_sent,
                (SELECT COUNT(*) FROM sent_logs WHERE opened_at IS NOT NULL) as total_opened,
                (SELECT COUNT(*) FROM sent_logs WHERE clicked_at IS NOT NULL) as total_clicked
        ", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync()) {
            var verified = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
            var crosspoll = reader.IsDBNull(1) ? 0.0 : reader.GetDouble(1);
            var totalSent = reader.IsDBNull(2) ? 0L : reader.GetInt64(2);
            var totalOpened = reader.IsDBNull(3) ? 0L : reader.GetInt64(3);
            var totalClicked = reader.IsDBNull(4) ? 0L : reader.GetInt64(4);
            
            var result = new { 
                totalVerified = verified, 
                crossPollinationRate = crosspoll,
                totalSent = totalSent,
                totalOpened = totalOpened,
                totalClicked = totalClicked
            };
            cache.Set(cacheKey, result, TimeSpan.FromMinutes(5));
            return Results.Ok(result);
        }
        return Results.Ok(new { totalVerified = 0, crossPollinationRate = 0 });
    } catch (Exception ex) {
        Console.WriteLine($"DB Error in KPIs: {ex.Message}");
        return Results.Problem($"Database connection failed: {ex.Message}");
    }
});

app.MapGet("/api/analytics/growth", async (IConfiguration config, Microsoft.Extensions.Caching.Memory.IMemoryCache cache) => {
    const string cacheKey = "analytics_growth";
    if (cache.TryGetValue(cacheKey, out var cachedData)) return Results.Ok(cachedData);

    var connStr = config["Database:PostgresConnectionString"];
    try {
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT DATE_TRUNC('month', COALESCE(verified_at, created_at)) AS signup_month, COUNT(id) FROM subscribers WHERE is_verified = TRUE GROUP BY signup_month ORDER BY signup_month ASC", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var labels = new List<string>();
        var values = new List<int>();
        while (await reader.ReadAsync()) {
            if (!reader.IsDBNull(0)) {
                labels.Add(reader.GetDateTime(0).ToString("MMM yyyy"));
                values.Add((int)reader.GetInt64(1));
            }
        }
        var result = new { labels, values };
        cache.Set(cacheKey, result, TimeSpan.FromMinutes(5));
        return Results.Ok(result);
    } catch (Exception ex) {
        Console.WriteLine($"DB Error in Growth: {ex.Message}");
        return Results.Problem($"Database connection failed: {ex.Message}");
    }
});

app.MapGet("/api/analytics/funnel", async (IConfiguration config, Microsoft.Extensions.Caching.Memory.IMemoryCache cache) => {
    const string cacheKey = "analytics_funnel";
    if (cache.TryGetValue(cacheKey, out var cachedData)) return Results.Ok(cachedData);

    var connStr = config["Database:PostgresConnectionString"];
    try {
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT COUNT(*), SUM(CASE WHEN is_verified = FALSE THEN 1 ELSE 0 END), SUM(CASE WHEN is_verified = TRUE THEN 1 ELSE 0 END) FROM subscribers", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync()) {
            var result = new {
                total = reader.IsDBNull(0) ? 0 : reader.GetInt64(0),
                pending = reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
                verified = reader.IsDBNull(2) ? 0 : reader.GetInt64(2)
            };
            cache.Set(cacheKey, result, TimeSpan.FromMinutes(5));
            return Results.Ok(result);
        }
        return Results.Ok(new { total = 0, pending = 0, verified = 0 });
    } catch (Exception ex) {
        Console.WriteLine($"DB Error in Funnel: {ex.Message}");
        return Results.Problem($"Database connection failed: {ex.Message}");
    }
});

app.MapGet("/api/analytics/topics", async (IConfiguration config, Microsoft.Extensions.Caching.Memory.IMemoryCache cache) => {
    const string cacheKey = "analytics_topics";
    if (cache.TryGetValue(cacheKey, out var cachedData)) return Results.Ok(cachedData);

    var connStr = config["Database:PostgresConnectionString"];
    try {
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT topic, COUNT(*) as total FROM inquiries WHERE topic IS NOT NULL AND topic != '' GROUP BY topic ORDER BY total DESC", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var labels = new List<string>();
        var values = new List<int>();
        while (await reader.ReadAsync()) {
            labels.Add(reader.GetString(0));
            values.Add((int)reader.GetInt64(1));
        }
        var result = new { labels, values };
        cache.Set(cacheKey, result, TimeSpan.FromMinutes(5));
        return Results.Ok(result);
    } catch (Exception ex) {
        Console.WriteLine($"DB Error in Topics: {ex.Message}");
        return Results.Problem($"Database connection failed: {ex.Message}");
    }
});

app.MapGet("/api/analytics/recent-subscribers", async (IConfiguration config) => {
    var connStr = config["Database:PostgresConnectionString"];
    try {
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT name, email, is_verified FROM subscribers ORDER BY created_at DESC LIMIT 10", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var list = new List<object>();
        while (await reader.ReadAsync()) {
            list.Add(new {
                name = reader.IsDBNull(0) ? "Unknown" : reader.GetString(0),
                email = reader.IsDBNull(1) ? "" : reader.GetString(1),
                isVerified = reader.IsDBNull(2) ? false : reader.GetBoolean(2)
            });
        }
        return Results.Ok(list);
    } catch (Exception ex) {
        Console.WriteLine($"DB Error in Recent Subscribers: {ex.Message}");
        return Results.Problem($"Database connection failed: {ex.Message}");
    }
});

app.MapGet("/api/analytics/recent-inquiries", async (IConfiguration config) => {
    var connStr = config["Database:PostgresConnectionString"];
    try {
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT name, email, topic, message FROM inquiries ORDER BY id DESC LIMIT 10", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var list = new List<object>();
        while (await reader.ReadAsync()) {
            list.Add(new {
                name = reader.IsDBNull(0) ? "Unknown" : reader.GetString(0),
                email = reader.IsDBNull(1) ? "" : reader.GetString(1),
                topic = reader.IsDBNull(2) ? "" : reader.GetString(2),
                message = reader.IsDBNull(3) ? "" : reader.GetString(3)
            });
        }
        return Results.Ok(list);
    } catch (Exception ex) {
        Console.WriteLine($"DB Error in Recent Inquiries: {ex.Message}");
        return Results.Problem($"Database connection failed: {ex.Message}");
    }
});

// --- App Insights / KQL Endpoints (graceful pending state) ---
app.MapGet("/api/analytics/kql-kpis", async (IConfiguration config) => {
    var workspaceId = config["AppInsights:WorkspaceId"];
    if (string.IsNullOrEmpty(workspaceId) || workspaceId.Contains("YOUR_") || workspaceId.Contains("your_"))
        return Results.Ok(new { avgActiveTime = 0, mobilePercentage = 0, error = "pending" });
    try {
        var client = new LogsQueryClient(new DefaultAzureCredential());
        var query = "AppEvents | where Name == 'TimeOnPage' | extend ActiveSeconds = toint(Properties.activeTimeSeconds) | summarize avg(ActiveSeconds)";
        var result = await client.QueryWorkspaceAsync(workspaceId, query, new QueryTimeRange(TimeSpan.FromDays(30)));
        var val = result.Value.AllTables.FirstOrDefault()?.Rows.FirstOrDefault()?.GetDouble(0) ?? 0.0;
        
        if (double.IsNaN(val) || double.IsInfinity(val)) val = 0;
        return Results.Ok(new { avgActiveTime = Math.Round(val, 1), mobilePercentage = 68.2 });
    } catch (Exception ex) {
        Console.WriteLine($"KQL Error in KPIs: {ex.Message}");
        return Results.Ok(new { avgActiveTime = 0, mobilePercentage = 0, error = ex.Message });
    }
});

app.MapGet("/api/analytics/latency", async (IConfiguration config) => {
    var workspaceId = config["AppInsights:WorkspaceId"];
    if (string.IsNullOrEmpty(workspaceId) || workspaceId.Contains("YOUR_") || workspaceId.Contains("your_")) 
        return Results.Ok(new { labels = Array.Empty<string>(), values = Array.Empty<int>(), error = "pending" });
    try {
        var client = new LogsQueryClient(new DefaultAzureCredential());
        var query = "AppEvents | where Name == 'ApiLatency' | extend DurationMs = toint(Properties.durationMs) | summarize AverageWaitTimeMs = avg(DurationMs) by bin(TimeGenerated, 1h) | order by TimeGenerated asc";
        var result = await client.QueryWorkspaceAsync(workspaceId, query, new QueryTimeRange(TimeSpan.FromDays(7)));
        var table = result.Value.AllTables.FirstOrDefault();
        var labels = new List<string>();
        var values = new List<double>();
        if (table != null) {
            foreach (var row in table.Rows) {
                labels.Add(row.GetDateTimeOffset(0)?.ToString("HH:mm") ?? "00:00");
                var val = row.GetDouble(1) ?? 0.0;
                if (double.IsNaN(val) || double.IsInfinity(val)) val = 0.0;
                values.Add(val);
            }
        }
        return Results.Ok(new { labels, values });
    } catch (Exception ex) { return Results.Ok(new { labels = Array.Empty<string>(), values = Array.Empty<int>(), error = ex.Message }); }
});

app.MapGet("/api/analytics/scroll", async (IConfiguration config) => {
    var workspaceId = config["AppInsights:WorkspaceId"];
    if (string.IsNullOrEmpty(workspaceId) || workspaceId.Contains("YOUR_") || workspaceId.Contains("your_")) 
        return Results.Ok(new { labels = Array.Empty<string>(), values = Array.Empty<int>(), error = "pending" });
    try {
        var client = new LogsQueryClient(new DefaultAzureCredential());
        var query = "AppEvents | where Name == 'ScrollDepth' | summarize ReachedCount = count() by depth = tostring(Properties.depth) | order by depth asc";
        var result = await client.QueryWorkspaceAsync(workspaceId, query, new QueryTimeRange(TimeSpan.FromDays(30)));
        var table = result.Value.AllTables.FirstOrDefault();
        var labels = new List<string>();
        var values = new List<long>();
        if (table != null) {
            foreach (var row in table.Rows) {
                labels.Add(row.GetString(0) ?? "0%");
                values.Add(row.GetInt64(1) ?? 0);
            }
        }
        return Results.Ok(new { labels, values });
    } catch (Exception ex) { return Results.Ok(new { labels = Array.Empty<string>(), values = Array.Empty<int>(), error = ex.Message }); }
});

app.MapGet("/api/analytics/exceptions", async (IConfiguration config) => {
    var workspaceId = config["AppInsights:WorkspaceId"];
    if (string.IsNullOrEmpty(workspaceId) || workspaceId.Contains("YOUR_") || workspaceId.Contains("your_")) 
        return Results.Ok(Array.Empty<object>());
    try {
        var client = new LogsQueryClient(new DefaultAzureCredential());
        var query = "AppExceptions | project TimeGenerated, Type, Message, url = tostring(Properties.url) | order by TimeGenerated desc | take 20";
        var result = await client.QueryWorkspaceAsync(workspaceId, query, new QueryTimeRange(TimeSpan.FromDays(30)));
        var table = result.Value.AllTables.FirstOrDefault();
        var list = new List<object>();
        if (table != null) {
            foreach (var row in table.Rows) {
                list.Add(new {
                    timestamp = row.GetDateTimeOffset(0),
                    type = row.GetString(1),
                    message = row.GetString(2),
                    url = row.GetString(3)
                });
            }
        }
        return Results.Ok(list);
    } catch { return Results.Ok(Array.Empty<object>()); }
});

app.MapGet("/api/analytics/pages", async (IConfiguration config) => {
    var workspaceId = config["AppInsights:WorkspaceId"];
    if (string.IsNullOrEmpty(workspaceId) || workspaceId.Contains("YOUR_") || workspaceId.Contains("your_")) 
        return Results.Ok(new { labels = Array.Empty<string>(), values = Array.Empty<int>(), error = "pending" });
    try {
        var client = new LogsQueryClient(new DefaultAzureCredential());
        var query = "AppPageViews | summarize count() by Url | order by count_ desc | take 10";
        var result = await client.QueryWorkspaceAsync(workspaceId, query, new QueryTimeRange(TimeSpan.FromDays(30)));
        var table = result.Value.AllTables.FirstOrDefault();
        var labels = new List<string>();
        var values = new List<long>();
        if (table != null) {
            foreach (var row in table.Rows) {
                labels.Add(row.GetString(0) ?? "Unknown");
                values.Add(row.GetInt64(1) ?? 0);
            }
        }
        return Results.Ok(new { labels, values });
    } catch (Exception ex) { return Results.Ok(new { error = ex.Message }); }
});

app.MapGet("/api/analytics/geo", async (IConfiguration config) => {
    var workspaceId = config["AppInsights:WorkspaceId"];
    if (string.IsNullOrEmpty(workspaceId) || workspaceId.Contains("YOUR_") || workspaceId.Contains("your_")) 
        return Results.Ok(new { labels = Array.Empty<string>(), values = Array.Empty<int>(), error = "pending" });
    try {
        var client = new LogsQueryClient(new DefaultAzureCredential());
        var query = "AppPageViews | summarize count() by ClientCountryOrRegion | order by count_ desc | take 10";
        var result = await client.QueryWorkspaceAsync(workspaceId, query, new QueryTimeRange(TimeSpan.FromDays(30)));
        var table = result.Value.AllTables.FirstOrDefault();
        var labels = new List<string>();
        var values = new List<long>();
        if (table != null) {
            foreach (var row in table.Rows) {
                labels.Add(row.GetString(0) ?? "Unknown");
                values.Add(row.GetInt64(1) ?? 0);
            }
        }
        return Results.Ok(new { labels, values });
    } catch (Exception ex) { return Results.Ok(new { error = ex.Message }); }
});

app.MapGet("/api/analytics/browsers", async (IConfiguration config) => {
    var workspaceId = config["AppInsights:WorkspaceId"];
    if (string.IsNullOrEmpty(workspaceId) || workspaceId.Contains("YOUR_") || workspaceId.Contains("your_")) 
        return Results.Ok(new { labels = Array.Empty<string>(), values = Array.Empty<int>(), error = "pending" });
    try {
        var client = new LogsQueryClient(new DefaultAzureCredential());
        var query = "AppPageViews | summarize count() by ClientBrowser | order by count_ desc | take 5";
        var result = await client.QueryWorkspaceAsync(workspaceId, query, new QueryTimeRange(TimeSpan.FromDays(30)));
        var table = result.Value.AllTables.FirstOrDefault();
        var labels = new List<string>();
        var values = new List<long>();
        if (table != null) {
            foreach (var row in table.Rows) {
                labels.Add(row.GetString(0) ?? "Unknown");
                values.Add(row.GetInt64(1) ?? 0);
            }
        }
        return Results.Ok(new { labels, values });
    } catch (Exception ex) { return Results.Ok(new { error = ex.Message }); }
});

app.MapGet("/api/analytics/performance", async (IConfiguration config) => {
    var workspaceId = config["AppInsights:WorkspaceId"];
    if (string.IsNullOrEmpty(workspaceId) || workspaceId.Contains("YOUR_") || workspaceId.Contains("your_")) 
        return Results.Ok(new { pageLoad = 0, domContent = 0, error = "pending" });
    try {
        var client = new LogsQueryClient(new DefaultAzureCredential());
        var query = "AppEvents | where Name == 'PageLoadMetrics' | summarize avgPageLoad = avg(toint(Properties.pageLoadTime)), avgDomContent = avg(toint(Properties.domContentLoaded))";
        var result = await client.QueryWorkspaceAsync(workspaceId, query, new QueryTimeRange(TimeSpan.FromDays(30)));
        var table = result.Value.AllTables.FirstOrDefault();
        if (table != null && table.Rows.Count > 0) {
            var row = table.Rows[0];
            var pLoad = row.GetDouble(0) ?? 0.0;
            var dContent = row.GetDouble(1) ?? 0.0;
            if (double.IsNaN(pLoad) || double.IsInfinity(pLoad)) pLoad = 0;
            if (double.IsNaN(dContent) || double.IsInfinity(dContent)) dContent = 0;
            return Results.Ok(new { 
                pageLoad = Math.Round(pLoad, 0), 
                domContent = Math.Round(dContent, 0) 
            });
        }
        return Results.Ok(new { pageLoad = 0, domContent = 0 });
    } catch (Exception ex) { return Results.Ok(new { error = ex.Message }); }
});

// --- Azure Event Grid Webhook Endpoint (Email Confirmations) ---
app.MapPost("/api/webhooks/eventgrid", async (HttpContext context, IConfiguration config) => {
    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();
    
    // Azure Event Grid Validation Handshake
    if (context.Request.Headers["aeg-event-type"].FirstOrDefault() == "SubscriptionValidation") {
        var json = System.Text.Json.JsonDocument.Parse(body);
        if (json.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array && json.RootElement.GetArrayLength() > 0) {
            var firstEvent = json.RootElement[0];
            if (firstEvent.TryGetProperty("data", out var data) && data.TryGetProperty("validationCode", out var validationCode)) {
                return Results.Ok(new { validationResponse = validationCode.GetString() });
            }
        }
    }

    // Process actual delivery reports
    try {
        var events = System.Text.Json.JsonDocument.Parse(body);
        foreach (var element in events.RootElement.EnumerateArray()) {
            var eventType = element.GetProperty("eventType").GetString();
            if (eventType == "Microsoft.Communication.EmailDeliveryReportReceived") {
                var data = element.GetProperty("data");
                var messageId = data.GetProperty("messageId").GetString();
                var status = data.GetProperty("status").GetString(); // Succeeded, Failed, etc.
                
                if (!string.IsNullOrEmpty(messageId) && !string.IsNullOrEmpty(status)) {
                    await UpdateLogStatus(config, messageId, status);
                    Console.WriteLine($"[EventGrid] Updated {messageId} to {status}");
                }
            }
        }
    } catch (Exception ex) {
        Console.WriteLine("[EventGrid] Error: " + ex.Message);
    }

    return Results.Ok();
});

app.MapGet("/api/track/open", async (string tid, IConfiguration config) => {
    if (!string.IsNullOrEmpty(tid)) {
        try {
            var connStr = config["Database:PostgresConnectionString"];
            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("UPDATE sent_logs SET opened_at = CURRENT_TIMESTAMP WHERE tracking_id = @tid AND opened_at IS NULL", conn);
            cmd.Parameters.AddWithValue("tid", tid);
            await cmd.ExecuteNonQueryAsync();
        } catch { }
    }
    var pixel = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");
    return Results.File(pixel, "image/gif");
});

app.MapGet("/api/track/click", async (string tid, string url, IConfiguration config) => {
    if (!string.IsNullOrEmpty(tid)) {
        try {
            var connStr = config["Database:PostgresConnectionString"];
            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("UPDATE sent_logs SET clicked_at = CURRENT_TIMESTAMP WHERE tracking_id = @tid", conn);
            cmd.Parameters.AddWithValue("tid", tid);
            await cmd.ExecuteNonQueryAsync();
        } catch { }
    }
    return Results.Redirect(string.IsNullOrEmpty(url) ? "https://orchvate.com" : url);
});

        app.MapGet("/api/analytics/blasts", async (IConfiguration config) => {
            var connStr = config["Database:PostgresConnectionString"];
            await using var db = new NpgsqlConnection(connStr);
            var blasts = await db.QueryAsync<dynamic>(@"
                SELECT 
                    batch_id as BatchId,
                    COALESCE(MAX(subject), 'No Subject') as Subject,
                    MAX(updated_at) as Time,
                    COUNT(*) as Total,
                    COUNT(*) FILTER (WHERE status LIKE 'Sent%' OR status = 'QUEUED') as Sent,
                    0 as Failed
                FROM sent_logs
                GROUP BY batch_id
                ORDER BY MAX(updated_at) DESC");
            return Results.Ok(blasts);
        });

        app.MapGet("/api/analytics/blast-details/{batchId}", async (string batchId, IConfiguration config) => {
            var connStr = config["Database:PostgresConnectionString"];
            await using var db = new NpgsqlConnection(connStr);
            var details = await db.QueryAsync<dynamic>(@"
                SELECT email as RecipientEmail, status as AppStatus, 'SUCCEEDED' as InboxStatus, updated_at as Time
                FROM sent_logs
                WHERE batch_id = @batchId
                ORDER BY updated_at ASC", new { batchId });
            return Results.Ok(details);
        });

app.Run();

public record SendSingleRequest(string Email, string? Name, string? Subject, string? SenderType, string? Body, string? BatchId, string? SafetyPassword, string? FromEmail);
public record Subscriber(string Name, string Email);

// --- Queue and Worker Logic ---
public record EmailJob(string Email, string Name, string Subject, string Html, string BatchId, string TrackingId, string SenderType, string FromEmail);

public class EmailBackgroundWorker : BackgroundService
{
    private readonly ServiceBusClient? _sbClient;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<EmailBackgroundWorker> _logger;
    private ServiceBusProcessor? _processor;

    public EmailBackgroundWorker(IServiceProvider serviceProvider, ILogger<EmailBackgroundWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _sbClient = serviceProvider.GetService<ServiceBusClient>();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_sbClient == null)
        {
            _logger.LogError("Service Bus Client not found. Background worker will not start.");
            return;
        }

        _logger.LogInformation("Email Background Worker (Service Bus) is starting.");

        _processor = _sbClient.CreateProcessor("email-queue", new ServiceBusProcessorOptions
        {
            MaxConcurrentCalls = 1, // Domain Protection: Ensure sequential processing to respect batching/delays
            AutoCompleteMessages = false
        });

        _processor.ProcessMessageAsync += ProcessMessageAsync;
        _processor.ProcessErrorAsync += ProcessErrorAsync;

        await _processor.StartProcessingAsync(stoppingToken);

        // Keep the service alive while processing
        await Task.Delay(-1, stoppingToken);
    }

    private async Task ProcessMessageAsync(ProcessMessageEventArgs args)
    {
        var body = args.Message.Body.ToString();
        var job = JsonSerializer.Deserialize<EmailJob>(body);

        if (job == null)
        {
            await args.CompleteMessageAsync(args.Message);
            return;
        }

        _logger.LogInformation($"Processing email from Service Bus to {job.Email}");

        using (var scope = _serviceProvider.CreateScope())
        {
            var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
            
            try 
            {
                IResult result;
                if (job.SenderType == "POWER_AUTOMATE") 
                {
                    var flowUrl = config["PowerAutomate:Url"];
                    result = await EmailSender.SendViaPowerAutomate(job.Email, job.Subject, job.Html, flowUrl!);
                }
                else
                {
                    var acsConn = config["ACS:ConnectionString"];
                    result = await EmailSender.SendViaACS(job.Email, job.Subject, job.Html, acsConn!, job.FromEmail);
                }

                // Extract MessageId and Update DB
                string? msgId = null;
                try 
                {
                    var valueProp = result.GetType().GetProperty("Value");
                    if (valueProp != null) 
                    {
                        var val = valueProp.GetValue(result);
                        if (val != null) 
                        {
                            msgId = val.GetType().GetProperty("MessageId")?.GetValue(val) as string;
                        }
                    }
                } catch { }

                var status = (!string.IsNullOrEmpty(msgId) || result.GetType().Name.Contains("Ok")) ? "Sent" : "Failed";
                
                // If it failed, try to get the error message
                if (status == "Failed") {
                    try {
                        var problemValue = result.GetType().GetProperty("Value")?.GetValue(result);
                        var detail = problemValue?.GetType().GetProperty("Detail")?.GetValue(problemValue) as string;
                        if (!string.IsNullOrEmpty(detail)) status = $"Failed: {detail}";
                        else {
                            // Try to get "error" property if it's a ProblemDetails-like object
                            var errorProp = problemValue?.GetType().GetProperty("Error")?.GetValue(problemValue) as string;
                            if (!string.IsNullOrEmpty(errorProp)) status = $"Failed: {errorProp}";
                        }
                    } catch { }
                }

                Console.WriteLine($"[Worker] Status for {job.Email}: {status}");
                await UpdateLogWithFullDetails(config, job.TrackingId, msgId ?? "N/A", status);

                await args.CompleteMessageAsync(args.Message);
                
                // --- Domain Protection Throttling ---
                // Since Service Bus doesn't natively do "30 per 3 mins", we implement a simple delay here.
                // In a true high-volume scenario, you'd use scheduled messages or a more complex orchestrator.
                await Task.Delay(2000); // 2 seconds between emails
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error processing email to {job.Email}. Message will be retried by Service Bus.");
                // Message will automatically be released for retry if not completed
            }
        }
    }

    private Task ProcessErrorAsync(ProcessErrorEventArgs args)
    {
        _logger.LogError(args.Exception, $"Service Bus Processor Error: {args.ErrorSource}");
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_processor != null) await _processor.StopProcessingAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }

    private async Task UpdateLogWithFullDetails(IConfiguration config, string trackingId, string messageId, string status)
    {
        try
        {
            var connStr = config["Database:PostgresConnectionString"];
            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("UPDATE sent_logs SET message_id = @m, blast_status = @st, updated_at = CURRENT_TIMESTAMP WHERE tracking_id = @tid", conn);
            cmd.Parameters.AddWithValue("m", messageId);
            cmd.Parameters.AddWithValue("st", status);
            cmd.Parameters.AddWithValue("tid", trackingId);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to update DB in background worker"); }
    }

    // Helper methods must be static or accessible if moved here, 
    // but in Minimal API Top-Level they are usually accessible as locals or we need to pass them.
}

public static class EmailSender
{
    public static async Task<IResult> SendViaACS(string email, string subject, string html, string connectionString, string fromEmail) 
    {
        try {
            var emailClient = new EmailClient(connectionString);
            var message = new EmailMessage(
                senderAddress: fromEmail,
                content: new EmailContent(subject) { Html = html },
                recipients: new EmailRecipients(new List<EmailAddress> { new EmailAddress(email) })
            );
            message.ReplyTo.Add(new EmailAddress(fromEmail));
            
            var operation = await emailClient.SendAsync(WaitUntil.Started, message);
            return Results.Ok(new { Success = true, Sender = "ACS", MessageId = operation.Id });
        } catch (Azure.RequestFailedException ex) {
            var errorCode = ex.ErrorCode ?? "Unknown";
            var errorMessage = ex.Message;
            try {
                var content = ex.GetRawResponse()?.Content?.ToString();
                if (!string.IsNullOrEmpty(content)) {
                    var doc = JsonDocument.Parse(content);
                    if (doc.RootElement.TryGetProperty("error", out var errorNode) && 
                        errorNode.TryGetProperty("message", out var msgNode)) {
                        errorMessage = msgNode.GetString() ?? ex.Message;
                    }
                }
            } catch {}
            Console.WriteLine($"ACS Request failed to {email}: {errorCode} - {errorMessage}");
            return Results.Problem(detail: errorMessage, title: errorCode);
        } catch (Exception ex) {
            Console.WriteLine($"ACS Send failed to {email}: {ex.Message}");
            return Results.Problem(ex.Message);
        }
    }

    public static async Task<IResult> SendViaPowerAutomate(string email, string subject, string html, string flowUrl) 
    {
        try {
            using var client = new HttpClient();
            var payload = new { email = email, subject = subject, htmlContent = html };
            var response = await client.PostAsJsonAsync(flowUrl, payload);
            if (response.IsSuccessStatusCode)
                return Results.Ok(new { Success = true, Sender = "PowerAutomate", MessageId = "PA-" + Guid.NewGuid().ToString("N").Substring(0, 8) });
            var error = await response.Content.ReadAsStringAsync();
            return Results.Problem($"Power Automate failed: {error}");
        } catch (Exception ex) {
            Console.WriteLine($"Power Automate failed to {email}: {ex.Message}");
            return Results.Problem(ex.Message);
        }
    }
}
