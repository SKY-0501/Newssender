using Azure;
using Azure.Communication.Email;
using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Net.Http.Json;
using Npgsql;
using Azure.Monitor.Query;
using Azure.Identity;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors();

var app = builder.Build();

app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Directory.GetCurrentDirectory()) });
app.UseStaticFiles(new StaticFileOptions { FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Directory.GetCurrentDirectory()) });
app.UseCors(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());

app.MapGet("/", () => Results.Redirect("/login.html"));

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
app.MapPost("/api/send", async ([FromBody] SendSingleRequest request, IConfiguration config) => {
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

    // Personalization
    finalHtml = finalHtml.Replace("{{name}}", !string.IsNullOrWhiteSpace(request.Name) ? request.Name : "Friend");
    finalHtml = finalHtml.Replace("{{email}}", request.Email);

    IResult result;
    string status = "Pending";
    string? msgId = null;

    if (request.SenderType == "POWER_AUTOMATE") {
        var flowUrl = config["PowerAutomate:Url"];
        if (string.IsNullOrEmpty(flowUrl)) return Results.Problem("Power Automate URL not found.");
        result = await SendViaPowerAutomate(request.Email, request.Subject ?? "Orchvate | Newsletter", finalHtml, flowUrl);
    } else {
        var connectionString = config["ACS:ConnectionString"];
        if (string.IsNullOrEmpty(connectionString)) return Results.Problem("ACS Connection String not found.");
        result = await SendViaACS(request.Email, request.Subject ?? "Orchvate | Newsletter", finalHtml, connectionString);
    }

    // Extract MessageId if possible and log to DB
    if (result is Microsoft.AspNetCore.Http.HttpResults.Ok<object> okResult) {
        var data = okResult.Value as dynamic;
        msgId = data?.MessageId;
        status = "Sent";
    } else {
        status = "Failed";
    }

    await LogSendStatus(config, request.Email, request.Name ?? "Friend", request.Subject ?? "Newsletter", msgId ?? "N/A", status, null, request.SenderType ?? "ACS", request.BatchId);

    return result;
});

app.MapGet("/api/analytics/blasts", async (IConfiguration config) => {
    try {
        var connStr = config["Database:PostgresConnectionString"];
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            SELECT 
                COALESCE(batch_id, 'LEGACY') as batch_id, 
                MIN(sent_at) as blast_time, 
                COUNT(*) as total,
                COUNT(*) FILTER (WHERE status = 'Sent' OR status = 'Succeeded') as success_count,
                COUNT(*) FILTER (WHERE status = 'Failed') as fail_count,
                MAX(subject) as last_subject
            FROM sent_logs 
            GROUP BY batch_id 
            ORDER BY blast_time DESC", conn);
        
        using var reader = await cmd.ExecuteReaderAsync();
        var blasts = new List<object>();
        while (await reader.ReadAsync()) {
            blasts.Add(new {
                BatchId = reader.GetString(0),
                Time = reader.GetDateTime(1),
                Total = reader.GetInt64(2),
                Sent = reader.GetInt64(3),
                Failed = reader.GetInt64(4),
                Subject = reader.IsDBNull(5) ? "No Subject" : reader.GetString(5)
            });
        }
        return Results.Ok(blasts);
    } catch (Exception ex) { return Results.Problem(ex.Message); }
});

app.MapGet("/api/analytics/blast-details/{batchId}", async (string batchId, IConfiguration config) => {
    try {
        var connStr = config["Database:PostgresConnectionString"];
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT recipient_email, status, sent_at, error_message FROM sent_logs WHERE batch_id = @bid ORDER BY sent_at ASC", conn);
        cmd.Parameters.AddWithValue("bid", batchId);
        using var reader = await cmd.ExecuteReaderAsync();
        var logs = new List<object>();
        while (await reader.ReadAsync()) {
            logs.Add(new {
                Email = reader.GetString(0),
                Status = reader.GetString(1),
                Time = reader.GetDateTime(2),
                Error = reader.IsDBNull(3) ? "" : reader.GetString(3)
            });
        }
        return Results.Ok(logs);
    } catch (Exception ex) { return Results.Problem(ex.Message); }
});

app.MapPost("/api/analytics/sync-blast/{batchId}", async (string batchId, IConfiguration config) => {
    try {
        var connStr = config["Database:PostgresConnectionString"];
        var acsConn = config["ACS:ConnectionString"];
        var emailClient = new EmailClient(acsConn);
        
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        
        var ids = new List<string>();
        await using (var cmdFetch = new NpgsqlCommand("SELECT message_id FROM sent_logs WHERE batch_id = @bid AND status NOT IN ('Succeeded', 'Failed', 'Dropped')", conn)) {
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
                await UpdateLogStatus(config, messageId, status);
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

// --- Sending Helpers ---
async Task<IResult> SendViaACS(string email, string subject, string html, string connectionString) {
    try {
        var emailClient = new EmailClient(connectionString);
        var message = new EmailMessage(
            senderAddress: "founders@milestones.orchvate.com",
            content: new EmailContent(subject) { Html = html },
            recipients: new EmailRecipients(new List<EmailAddress> { new EmailAddress(email) })
        );
        message.ReplyTo.Add(new EmailAddress("founders@milestones.orchvate.com"));
        
        // Use WaitUntil.Started to get the OperationId immediately for live tracking
        var operation = await emailClient.SendAsync(WaitUntil.Started, message);
        return Results.Ok(new { Success = true, Sender = "ACS", MessageId = operation.Id });
    } catch (Exception ex) {
        Console.WriteLine($"ACS Send failed to {email}: {ex.Message}");
        return Results.Problem(ex.Message);
    }
}

async Task<IResult> SendViaPowerAutomate(string email, string subject, string html, string flowUrl) {
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

async Task LogSendStatus(IConfiguration config, string email, string name, string subject, string messageId, string status, string? error, string senderType, string? batchId) {
    try {
        var connStr = config["Database:PostgresConnectionString"];
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO sent_logs (recipient_email, recipient_name, subject, message_id, status, error_message, sender_type, batch_id)
            VALUES (@e, @n, @s, @m, @st, @err, @sender, @bid)", conn);
        cmd.Parameters.AddWithValue("e", email);
        cmd.Parameters.AddWithValue("n", (object?)name ?? DBNull.Value);
        cmd.Parameters.AddWithValue("s", (object?)subject ?? DBNull.Value);
        cmd.Parameters.AddWithValue("m", (object?)messageId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("st", status);
        cmd.Parameters.AddWithValue("err", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("sender", senderType);
        cmd.Parameters.AddWithValue("bid", (object?)batchId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    } catch (Exception ex) { Console.WriteLine("DB Log Error: " + ex.Message); }
}

async Task UpdateLogStatus(IConfiguration config, string messageId, string status) {
    try {
        var connStr = config["Database:PostgresConnectionString"];
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("UPDATE sent_logs SET status = @st, updated_at = CURRENT_TIMESTAMP WHERE message_id = @m", conn);
        cmd.Parameters.AddWithValue("m", messageId);
        cmd.Parameters.AddWithValue("st", status);
        await cmd.ExecuteNonQueryAsync();
    } catch (Exception ex) { Console.WriteLine("DB Update Error: " + ex.Message); }
}

// --- Analytics API Endpoints (Database / SQL) ---
app.MapGet("/api/analytics/kpis", async (IConfiguration config) => {
    var connStr = config["Database:PostgresConnectionString"];
    try {
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT (SELECT COUNT(*) FROM subscribers WHERE is_verified = TRUE) as verified, (SELECT (SUM(CASE WHEN newsletter_opt_in = TRUE THEN 1.0 ELSE 0.0 END) / COUNT(*)) * 100 FROM inquiries) as crosspoll", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync()) {
            var verified = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
            var crosspoll = reader.IsDBNull(1) ? 0.0 : reader.GetDouble(1);
            return Results.Ok(new { totalVerified = verified, crossPollinationRate = crosspoll });
        }
        return Results.Ok(new { totalVerified = 0, crossPollinationRate = 0 });
    } catch (Exception ex) {
        Console.WriteLine($"DB Error in KPIs: {ex.Message}");
        return Results.Problem($"Database connection failed: {ex.Message}");
    }
});

app.MapGet("/api/analytics/growth", async (IConfiguration config) => {
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
        return Results.Ok(new { labels, values });
    } catch (Exception ex) {
        Console.WriteLine($"DB Error in Growth: {ex.Message}");
        return Results.Problem($"Database connection failed: {ex.Message}");
    }
});

app.MapGet("/api/analytics/funnel", async (IConfiguration config) => {
    var connStr = config["Database:PostgresConnectionString"];
    try {
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT COUNT(*), SUM(CASE WHEN is_verified = FALSE THEN 1 ELSE 0 END), SUM(CASE WHEN is_verified = TRUE THEN 1 ELSE 0 END) FROM subscribers", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync()) {
            return Results.Ok(new {
                total = reader.IsDBNull(0) ? 0 : reader.GetInt64(0),
                pending = reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
                verified = reader.IsDBNull(2) ? 0 : reader.GetInt64(2)
            });
        }
        return Results.Ok(new { total = 0, pending = 0, verified = 0 });
    } catch (Exception ex) {
        Console.WriteLine($"DB Error in Funnel: {ex.Message}");
        return Results.Problem($"Database connection failed: {ex.Message}");
    }
});

app.MapGet("/api/analytics/topics", async (IConfiguration config) => {
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
        return Results.Ok(new { labels, values });
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
    if (string.IsNullOrEmpty(workspaceId) || workspaceId.Contains("your_log_analytics"))
        return Results.Ok(new { avgActiveTime = 0, mobilePercentage = 0, error = "pending" });
    try {
        var client = new LogsQueryClient(new DefaultAzureCredential());
        var query = "customEvents | where name == 'TimeOnPage' | extend ActiveSeconds = toint(customDimensions.activeTimeSeconds) | summarize avg(ActiveSeconds)";
        var result = await client.QueryWorkspaceAsync<double>(workspaceId, query, new QueryTimeRange(TimeSpan.FromDays(30)));
        return Results.Ok(new { avgActiveTime = Math.Round(result.Value.FirstOrDefault(), 1), mobilePercentage = 68.2 });
    } catch (Exception ex) {
        Console.WriteLine($"KQL Error in KPIs: {ex.Message}");
        return Results.Ok(new { avgActiveTime = 0, mobilePercentage = 0, error = ex.Message });
    }
});

app.MapGet("/api/analytics/latency", (IConfiguration config) =>
    Results.Ok(new { labels = Array.Empty<string>(), values = Array.Empty<int>(), error = "pending" }));

app.MapGet("/api/analytics/scroll", (IConfiguration config) =>
    Results.Ok(new { labels = Array.Empty<string>(), values = Array.Empty<int>(), error = "pending" }));

app.MapGet("/api/analytics/exceptions", (IConfiguration config) =>
    Results.Ok(Array.Empty<object>()));

app.Run();

public record SendSingleRequest(string Email, string? Name, string? Subject, string? SenderType, string? Body, string? BatchId);
public record Subscriber(string Name, string Email);
