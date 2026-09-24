using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

var allowedRoots = builder.Configuration .GetSection("LogViewer:AllowedRoots") .GetChildren() .Select(x => x.Value) .Where(x => !string.IsNullOrWhiteSpace(x)) .Select(x => x!) .ToArray();

var defaultIisLogPath = builder.Configuration["IIS:DefaultLogPath"] ?? "";
var testIisFile = builder.Configuration["IIS TestFile"] ?? "";
if (!string.IsNullOrWhiteSpace(testIisFile))
    defaultIisLogPath = testIisFile;
var demoLogFile = Path.Combine(builder.Environment.ContentRootPath, "2026-08-05.txt");
var inboundConfiguredPath = builder.Configuration["InboundLogPath"] ?? "";
var outboundConfiguredPath = builder.Configuration["OutboundLogPath"] ?? "";
var inboundTestFile = GetTestFileConfiguration(builder.Configuration, "Inbound");
var outboundTestFile = GetTestFileConfiguration(builder.Configuration, "Outbound");
var inboundLogPath = ResolveLogDirectory(inboundConfiguredPath, inboundTestFile, demoLogFile);
var outboundLogPath = ResolveLogDirectory(outboundConfiguredPath, outboundTestFile, demoLogFile);
var locksApiBaseUrl = builder.Configuration["LocksApi:BaseUrl"] ?? "";
var locksApiUsername = builder.Configuration["LocksApi:Username"] ?? "";
var locksApiPassword = builder.Configuration["LocksApi:Password"] ?? "";

builder.Services.AddHttpClient();
// Omdirigeringer (f.eks. RequireHttps) skal vises som feil, ikke følges stille til en annen adresse.
builder.Services.AddHttpClient("LocksApi")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

var app = builder.Build();

app.MapGet("/", () => Results.Content(
    BuildTabbedHtml(
        "Ingen fil valgt",
        "Velg IIS-fanen for å laste IIS-oppsummeringen.",
        "Ingen response-fil valgt.",
        "Ingen metadata tilgjengelig.",
        defaultIisLogPath,
        inboundLogPath,
        outboundLogPath,
        "inbound"),
    "text/html",
    Encoding.UTF8));

app.MapGet("/view", async (HttpContext context) =>
{
    var file = context.Request.Query["file"].ToString();

    if (string.IsNullOrWhiteSpace(file))
    {
        return Results.Content(HtmlPage("Mangler parameter", "Parameteren file mangler."), "text/html", Encoding.UTF8);
    }

    file = WebUtility.UrlDecode(file);

    if (!IsAllowedPath(file, allowedRoots))
    {
        return Results.Content(HtmlPage("Ikke tillatt filsti", "Denne filstien er ikke tillatt:<br>" + WebUtility.HtmlEncode(file)), "text/html", Encoding.UTF8);
    }

    var openedInfo = ParseLogFileName(file);

    string? requestFile;
    string? responseFile;
    string matchingInfo;

    if (openedInfo != null && openedInfo.Kind.Equals("resp", StringComparison.OrdinalIgnoreCase))
    {
        responseFile = file;
        requestFile = FindRequestForResponse(file);
        matchingInfo = "Åpnet response-fil. Request matches med samme GUID og nærmeste request før response-tidspunktet.";
    }
    else
    {
        requestFile = file;
        responseFile = FindResponseForRequest(file);
        matchingInfo = "Åpnet request-fil. Response matches med samme GUID og nærmeste response etter request-tidspunktet.";
    }

    string requestText = "";
    bool requestFound = false;

    if (!string.IsNullOrWhiteSpace(requestFile) && System.IO.File.Exists(requestFile))
    {
        requestText = await ReadTextFile(requestFile);
        requestFound = true;
    }

    string responseText = "";
    bool responseFound = false;

    if (!string.IsNullOrWhiteSpace(responseFile) && System.IO.File.Exists(responseFile))
    {
        responseText = await ReadTextFile(responseFile);
        responseFound = true;
    }

    if (!requestFound && !responseFound)
    {
        return Results.Content(HtmlPage("Fil finnes ikke", "Fant ikke filen:<br>" + WebUtility.HtmlEncode(file)), "text/html", Encoding.UTF8);
    }

    var dataText = requestFound
        ? BuildDataTab(requestText)
        : "Fant ingen tilhørende request-fil for denne responsen.";

    var responseTabText = responseFound
        ? BuildResponseTab(responseText, true)
        : "Fant ingen tilhørende response-fil for denne requesten.";

    var metadataText = BuildMetadataTab(
        file,
        requestFile,
        responseFile,
        requestText,
        responseText,
        requestFound,
        responseFound,
        matchingInfo);

    var html = BuildTabbedHtml(file, dataText, responseTabText, metadataText, defaultIisLogPath, inboundLogPath, outboundLogPath);

    return Results.Content(html, "text/html", Encoding.UTF8);
});

// IIS dashboard and API
app.MapGet("/iis", (HttpContext context) =>
{
    return Results.Redirect("/");
});

app.MapGet("/api/iis/summary", async (HttpContext context) =>
{
    var file = context.Request.Query["file"].ToString();

    if (string.IsNullOrWhiteSpace(file))
        file = defaultIisLogPath;

    if (string.IsNullOrWhiteSpace(file))
        return Results.BadRequest(new { error = "Parameter 'file' mangler og ingen IIS:DefaultLogPath satt" });

    file = WebUtility.UrlDecode(file);

    if (!IsAllowedPath(file, allowedRoots))
        return Results.StatusCode(403);

    if (!System.IO.File.Exists(file))
        return Results.NotFound();

    try
    {
        var entries = await ParseIisLogFile(file);
        var metrics = ComputeIisMetrics(entries);
        return Results.Json(metrics, GetJsonOptions());
    }
    catch (Exception exception)
    {
        return Results.Json(new
        {
            error = "Klarte ikke å lese IIS-loggfilen.",
            file,
            detail = exception.Message,
            exceptionType = exception.GetType().Name
        }, statusCode: 500);
    }
});

app.MapGet("/api/iis/files", () =>
{
    try
    {
        return Results.Json(GetIisLogFiles(defaultIisLogPath, allowedRoots));
    }
    catch (Exception exception)
    {
        return Results.Json(new { error = "Klarte ikke å lese IIS-loggmappen.", detail = exception.Message }, statusCode: 500);
    }
});

app.MapGet("/api/inbound/files", () =>
{
    return Results.Json(GetDateLogFiles(inboundLogPath, inboundConfiguredPath, inboundTestFile, allowedRoots));
});

app.MapGet("/api/inbound/summary", async (HttpContext context) =>
{
    var date = context.Request.Query["date"].ToString();
    if (!DateTime.TryParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsedDate))
        return Results.BadRequest(new { error = "Dato må ha formatet yyyy-MM-dd" });

    var file = ResolveDateLogFile(inboundLogPath, inboundConfiguredPath, inboundTestFile, parsedDate);
    if (!IsAllowedPath(file, allowedRoots) || !System.IO.File.Exists(file))
        return Results.NotFound();

    var entries = await ParseInboundLogFile(file);
    return Results.Json(ComputeInboundMetrics(entries), GetJsonOptions());
});

app.MapGet("/api/outbound/files", () =>
    Results.Json(GetDateLogFiles(outboundLogPath, outboundConfiguredPath, outboundTestFile, allowedRoots)));

app.MapGet("/api/diagnostics/paths", () => Results.Json(new
{
    iis = GetPathDiagnostic(defaultIisLogPath, allowedRoots),
    inbound = GetPathDiagnostic(inboundLogPath, allowedRoots),
    outbound = GetPathDiagnostic(outboundLogPath, allowedRoots)
}));

app.MapGet("/api/outbound/summary", async (HttpContext context) =>
{
    var date = context.Request.Query["date"].ToString();
    if (!DateTime.TryParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsedDate))
        return Results.BadRequest(new { error = "Dato må ha formatet yyyy-MM-dd" });

    var file = ResolveDateLogFile(outboundLogPath, outboundConfiguredPath, outboundTestFile, parsedDate);
    if (!IsAllowedPath(file, allowedRoots) || !System.IO.File.Exists(file))
        return Results.NotFound();

    var entries = await ParseInboundLogFile(file);
    return Results.Json(ComputeInboundMetrics(entries), GetJsonOptions());
});

app.MapGet("/api/locks", async (IHttpClientFactory httpClientFactory) =>
{
    if (!TryCreateLocksClient(httpClientFactory, locksApiBaseUrl, locksApiUsername, locksApiPassword, out var client, out var configurationError))
        return Results.Json(new { error = configurationError }, statusCode: 503);

    try
    {
        var endpointTasks = new[]
        {
            GetLocksAsync(client, "api/Assignments/Locks", app.Logger),
            GetLocksAsync(client, "api/Postings/Locks", app.Logger),
            GetLocksAsync(client, "api/Remits/Locks", app.Logger),
            GetLocksAsync(client, "api/OutboundInvoices/Locks", app.Logger)
        };
        var lockGroups = await Task.WhenAll(endpointTasks);
        return Results.Json(lockGroups.SelectMany(group => group).OrderBy(lockItem => lockItem.TypeOfLock).ThenBy(lockItem => lockItem.InstallationId).ThenBy(lockItem => lockItem.LockId));
    }
    catch (LocksApiException exception)
    {
        return Results.Json(new { error = exception.Message }, statusCode: exception.StatusCode);
    }
});

app.MapPost("/api/locks/unlock", async (UnlockLockRequest request, IHttpClientFactory httpClientFactory) =>
{
    var endpoint = request.TypeOfLock switch
    {
        "Posting" => $"api/{Uri.EscapeDataString(request.InstallationId)}/Postings/Locks/{Uri.EscapeDataString(request.LockId)}/Unlock",
        "Remit" => $"api/{Uri.EscapeDataString(request.InstallationId)}/Remits/Locks/{Uri.EscapeDataString(request.LockId)}/Unlock",
        "OutboundInvoice" => $"api/{Uri.EscapeDataString(request.InstallationId)}/OutboundInvoices/Locks/{Uri.EscapeDataString(request.LockId)}/Unlock",
        _ => null
    };

    if (endpoint == null)
        return Results.BadRequest(new { error = "Denne låstypen kan ikke låses opp via API-et." });

    if (string.IsNullOrWhiteSpace(request.InstallationId) || string.IsNullOrWhiteSpace(request.LockId))
        return Results.BadRequest(new { error = "Installasjon og lås-ID må være utfylt." });

    if (!TryCreateLocksClient(httpClientFactory, locksApiBaseUrl, locksApiUsername, locksApiPassword, out var client, out var configurationError))
        return Results.Json(new { error = configurationError }, statusCode: 503);

    var requestUri = new Uri(client.BaseAddress!, endpoint);
    HttpResponseMessage response;
    try
    {
        response = await client.PostAsync(requestUri, new StringContent("{}", Encoding.UTF8, "application/json"));
    }
    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
    {
        app.Logger.LogWarning(exception, "Locks API: POST {Uri} feilet", requestUri);
        return Results.Json(new { error = $"Fikk ikke kontakt med {requestUri}: {exception.Message}" }, statusCode: 502);
    }

    using (response)
    {
        app.Logger.LogInformation("Locks API: POST {Uri} -> HTTP {StatusCode}", requestUri, (int)response.StatusCode);
        if (!response.IsSuccessStatusCode)
            return Results.Json(new { error = $"Unlock feilet mot {requestUri} (HTTP {(int)response.StatusCode}){DescribeRedirect(response)}." }, statusCode: IsRedirect(response) ? 502 : (int)response.StatusCode);
    }

    return Results.Ok(new { message = "Låsen er fjernet." });
});

app.Run();

static bool TryCreateLocksClient(
    IHttpClientFactory httpClientFactory,
    string baseUrl,
    string username,
    string password,
    out HttpClient client,
    out string error)
{
    client = httpClientFactory.CreateClient("LocksApi");
    error = "";

    if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) ||
        (baseUri.Scheme != Uri.UriSchemeHttps &&
         !(baseUri.Scheme == Uri.UriSchemeHttp &&
           (baseUri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || baseUri.Host == "127.0.0.1"))))
    {
        error = "LocksApi:BaseUrl må være HTTPS, med unntak av localhost.";
        return false;
    }

    if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
    {
        error = "LocksApi:Username og LocksApi:Password må være konfigurert.";
        return false;
    }

    // Avsluttende skråstrek sikrer at en ev. virtuell katalog i BaseUrl beholdes når relative endepunkter legges til.
    client.BaseAddress = baseUri.AbsoluteUri.EndsWith('/') ? baseUri : new Uri(baseUri.AbsoluteUri + "/");
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
        "Basic",
        Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}")));
    return true;
}

static async Task<List<SystemLockDto>> GetLocksAsync(HttpClient client, string endpoint, ILogger logger)
{
    var requestUri = new Uri(client.BaseAddress!, endpoint);
    HttpResponseMessage response;
    try
    {
        response = await client.GetAsync(requestUri);
    }
    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
    {
        logger.LogWarning(exception, "Locks API: GET {Uri} feilet", requestUri);
        throw new LocksApiException($"Fikk ikke kontakt med {requestUri}: {exception.Message}", 502);
    }

    using (response)
    {
        var body = await response.Content.ReadAsStringAsync();
        var snippet = body.Length > 300 ? body[..300] + "..." : body;
        logger.LogInformation("Locks API: GET {Uri} -> HTTP {StatusCode}: {Body}", requestUri, (int)response.StatusCode, snippet);

        if (!response.IsSuccessStatusCode)
            throw new LocksApiException($"Klarte ikke å hente låser fra {requestUri} (HTTP {(int)response.StatusCode}){DescribeRedirect(response)}.", IsRedirect(response) ? 502 : (int)response.StatusCode);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new LocksApiException($"Svaret fra {requestUri} er ikke gyldig JSON (HTTP {(int)response.StatusCode}): {snippet}", 502);
        }

        using (document)
        {
            var locks = new List<SystemLockDto>();
            ReadLocks(document.RootElement, locks);
            return locks;
        }
    }
}

static bool IsRedirect(HttpResponseMessage response)
{
    return (int)response.StatusCode is >= 300 and < 400;
}

static string DescribeRedirect(HttpResponseMessage response)
{
    return IsRedirect(response) && response.Headers.Location != null
        ? $" – API-et omdirigerer til {response.Headers.Location}. Sett LocksApi:BaseUrl til HTTPS-adressen til API-et"
        : "";
}

static void ReadLocks(JsonElement element, List<SystemLockDto> locks)
{
    if (element.ValueKind == JsonValueKind.Array)
    {
        foreach (var item in element.EnumerateArray())
            ReadLockItem(item, locks);
        return;
    }

    if (element.ValueKind == JsonValueKind.Object)
    {
        if (TryGetPropertyIgnoreCase(element, "lockId", out _))
        {
            ReadLockItem(element, locks);
            return;
        }

        foreach (var property in element.EnumerateObject())
            ReadLocks(property.Value, locks);
    }
}

static void ReadLockItem(JsonElement element, List<SystemLockDto> locks)
{
    if (element.ValueKind != JsonValueKind.Object)
        return;

    var installationId = GetJsonString(element, "installationId");
    var lockId = GetJsonString(element, "lockId");
    var typeOfLock = GetJsonString(element, "typeOfLock");
    if (!string.IsNullOrWhiteSpace(lockId))
        locks.Add(new SystemLockDto(installationId, lockId, typeOfLock, GetJsonString(element, "errorMessage")));
}

static string GetJsonString(JsonElement element, string name)
{
    return TryGetPropertyIgnoreCase(element, name, out var value) ? value.ToString() : "";
}

static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
{
    foreach (var property in element.EnumerateObject())
    {
        if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            value = property.Value;
            return true;
        }
    }

    value = default;
    return false;
}

static string GetTestFileConfiguration(IConfiguration configuration, string source)
{
    return new[]
    {
        configuration[$"{source} TestFile"],
        configuration[$"{source}TestFile"]
    }.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
}

static string ResolveLogDirectory(string configuredPath, string testFile, string demoLogFile)
{
    if (!string.IsNullOrWhiteSpace(configuredPath))
        return configuredPath;

    if (!string.IsNullOrWhiteSpace(testFile))
        return Path.GetDirectoryName(testFile) ?? Directory.GetCurrentDirectory();

    return Path.GetDirectoryName(demoLogFile) ?? Directory.GetCurrentDirectory();
}

static IEnumerable<object> GetDateLogFiles(string directory, string configuredPath, string testFile, string[] allowedRoots)
{
    if (!Directory.Exists(directory) || !IsAllowedPath(directory, allowedRoots))
        return Array.Empty<object>();

    if (string.IsNullOrWhiteSpace(configuredPath) && !string.IsNullOrWhiteSpace(testFile))
    {
        if (!System.IO.File.Exists(testFile) || !IsAllowedPath(testFile, allowedRoots))
            return Array.Empty<object>();

        var testFileName = Path.GetFileName(testFile);
        var testDate = Path.GetFileNameWithoutExtension(testFileName);
        return new[] { (object)new { file = testFileName, date = testDate } };
    }

    return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
        .Select(file => new
        {
            file = Path.GetFileName(file),
            date = Path.GetFileNameWithoutExtension(file)
        })
        .Where(x => x.file != null && DateTime.TryParseExact(x.date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
        .OrderByDescending(x => x.date)
        .Take(20)
        .Select(x => (object)new { x.file, x.date })
        .ToList();
}

static object GetPathDiagnostic(string path, string[] allowedRoots)
{
    var exists = Directory.Exists(path);
    var allowed = IsAllowedPath(path, allowedRoots);
    var fileCount = 0;

    if (exists && allowed)
    {
        try
        {
            fileCount = Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly).Count();
        }
        catch
        {
            fileCount = -1;
        }
    }

    return new
    {
        path,
        exists,
        allowed,
        fileCount,
        message = !exists
            ? "Mappen finnes ikke, eller IIS-kontoen har ikke tilgang."
            : !allowed
                ? "Mappen er ikke med i LogViewer:AllowedRoots."
                : fileCount < 0
                    ? "Mappen finnes, men kan ikke leses."
                    : $"Mappen kan leses og inneholder {fileCount} filer."
    };
}

static IEnumerable<object> GetIisLogFiles(string configuredPath, string[] allowedRoots)
{
    if (string.IsNullOrWhiteSpace(configuredPath))
        return Array.Empty<object>();

    var directory = Directory.Exists(configuredPath)
        ? configuredPath
        : Path.GetDirectoryName(configuredPath) ?? "";

    if (!Directory.Exists(directory) || !IsAllowedPath(directory, allowedRoots))
        return Array.Empty<object>();

    var files = new List<(string path, string file, DateTime modified)>();

    foreach (var file in Directory.EnumerateFiles(directory, "*.log", SearchOption.TopDirectoryOnly))
    {
        if (!IsAllowedPath(file, allowedRoots))
            continue;

        try
        {
            files.Add((file, Path.GetFileName(file), System.IO.File.GetLastWriteTime(file)));
        }
        catch (IOException)
        {
            // Skip files that disappear or cannot be inspected while the directory is scanned.
        }
        catch (UnauthorizedAccessException)
        {
            // Skip files the IIS worker identity cannot inspect.
        }
    }

    return files
        .OrderByDescending(x => x.modified)
        .Take(20)
        .Select(x => (object)new { x.path, x.file, modified = x.modified.ToString("yyyy-MM-dd HH:mm:ss") })
        .ToList();
}

static string ResolveDateLogFile(string directory, string configuredPath, string testFile, DateTime date)
{
    if (string.IsNullOrWhiteSpace(configuredPath) && !string.IsNullOrWhiteSpace(testFile))
        return testFile;

    var dateText = date.ToString("yyyy-MM-dd");
    if (Directory.Exists(directory))
    {
        var matchingFile = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(file => string.Equals(
                Path.GetFileNameWithoutExtension(file),
                dateText,
                StringComparison.OrdinalIgnoreCase));

        if (matchingFile != null)
            return matchingFile;
    }

    return Path.Combine(directory, dateText + ".txt");
}

static bool IsAllowedPath(string file, string[] allowedRoots) { if (string.IsNullOrWhiteSpace(file)) return false;

if (file.Contains(".."))
    return false;

// Lokal test, for eksempel test.txt
if (!file.StartsWith("\\\\") && !Path.IsPathRooted(file))
    return true;

if (allowedRoots == null || allowedRoots.Length == 0)
    return false;

var fullPath = Path.GetFullPath(file);

foreach (var root in allowedRoots)
{
    if (string.IsNullOrWhiteSpace(root))
        continue;

    var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    if (fullPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
        fullPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        return true;
}

return false;


}

static LogFileName? ParseLogFileName(string file)
{
    var name = Path.GetFileNameWithoutExtension(file);

    if (string.IsNullOrWhiteSpace(name))
        return null;

    var parts = name.Split('_', StringSplitOptions.RemoveEmptyEntries);

    if (parts.Length < 3)
        return null;

    var kind = parts[^1];
    var stampText = parts[^2];
    var id = string.Join("_", parts.Take(parts.Length - 2));

    if (!kind.Equals("req", StringComparison.OrdinalIgnoreCase) &&
        !kind.Equals("resp", StringComparison.OrdinalIgnoreCase))
        return null;

    if (!long.TryParse(stampText, out var stamp))
        return null;

    return new LogFileName(id, stamp, kind.ToLowerInvariant());
}

static string? FindResponseForRequest(string requestFile)
{
    var info = ParseLogFileName(requestFile);

    if (info == null || !info.Kind.Equals("req", StringComparison.OrdinalIgnoreCase))
        return null;

    var dir = Path.GetDirectoryName(requestFile);

    if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        return null;

    string? bestFile = null;
    long? bestStamp = null;

    foreach (var candidate in Directory.EnumerateFiles(dir, info.Id + "_*_resp.txt"))
    {
        var candidateInfo = ParseLogFileName(candidate);

        if (candidateInfo == null)
            continue;

        if (!candidateInfo.Id.Equals(info.Id, StringComparison.OrdinalIgnoreCase))
            continue;

        if (!candidateInfo.Kind.Equals("resp", StringComparison.OrdinalIgnoreCase))
            continue;

        // Riktig retry-match: første response etter request.
        if (candidateInfo.Stamp <= info.Stamp)
            continue;

        if (bestStamp == null || candidateInfo.Stamp < bestStamp.Value)
        {
            bestStamp = candidateInfo.Stamp;
            bestFile = candidate;
        }
    }

    return bestFile;
}

static string? FindRequestForResponse(string responseFile)
{
    var info = ParseLogFileName(responseFile);

    if (info == null || !info.Kind.Equals("resp", StringComparison.OrdinalIgnoreCase))
        return null;

    var dir = Path.GetDirectoryName(responseFile);

    if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        return null;

    string? bestFile = null;
    long? bestStamp = null;

    foreach (var candidate in Directory.EnumerateFiles(dir, info.Id + "_*_req.txt"))
    {
        var candidateInfo = ParseLogFileName(candidate);

        if (candidateInfo == null)
            continue;

        if (!candidateInfo.Id.Equals(info.Id, StringComparison.OrdinalIgnoreCase))
            continue;

        if (!candidateInfo.Kind.Equals("req", StringComparison.OrdinalIgnoreCase))
            continue;

        // Riktig retry-match: siste request før response.
        if (candidateInfo.Stamp >= info.Stamp)
            continue;

        if (bestStamp == null || candidateInfo.Stamp > bestStamp.Value)
        {
            bestStamp = candidateInfo.Stamp;
            bestFile = candidate;
        }
    }

    return bestFile;
}

static async Task<string> ReadTextFile(string file)
{
    try
    {
        return await System.IO.File.ReadAllTextAsync(file, Encoding.UTF8);
    }
    catch
    {
        return await System.IO.File.ReadAllTextAsync(file, Encoding.Default);
    }
}

static string BuildDataTab(string requestText)
{
    var content = ExtractContent(requestText);

    if (string.IsNullOrWhiteSpace(content))
        return "Fant ikke Content i request-filen.";

    var innerData = ExtractInnerDataJson(content);

    if (!string.IsNullOrWhiteSpace(innerData))
        return innerData;

    return TryFormatJson(content);
}

static string BuildResponseTab(string responseText, bool responseFound)
{
    if (!responseFound)
        return responseText;

    var metadata = ExtractMetadata(responseText);
    var content = ExtractContent(responseText);

    var sb = new StringBuilder();

    if (!string.IsNullOrWhiteSpace(metadata))
    {
        sb.AppendLine(metadata.Trim());
    }

    if (!string.IsNullOrWhiteSpace(content))
    {
        if (sb.Length > 0)
            sb.AppendLine();

        sb.AppendLine("Content:");
        sb.AppendLine(TryFormatJson(content));
    }

    if (sb.Length == 0)
        return responseText.Trim();

    return sb.ToString().Trim();
}

static string BuildMetadataTab(
    string openedFile,
    string? requestFile,
    string? responseFile,
    string requestText,
    string responseText,
    bool requestFound,
    bool responseFound,
    string matchingInfo)
{
    var sb = new StringBuilder();

    sb.AppendLine("Åpnet fil:");
    sb.AppendLine(openedFile);
    sb.AppendLine();

    sb.AppendLine("Matchingregel:");
    sb.AppendLine(matchingInfo);
    sb.AppendLine();

    if (!string.IsNullOrWhiteSpace(requestFile))
    {
        sb.AppendLine("Matchet request-fil:");
        sb.AppendLine(requestFile);
        sb.AppendLine(requestFound ? "Request-fil funnet." : "Request-fil ikke funnet.");
        sb.AppendLine();
    }

    if (!string.IsNullOrWhiteSpace(responseFile))
    {
        sb.AppendLine("Matchet response-fil:");
        sb.AppendLine(responseFile);
        sb.AppendLine(responseFound ? "Response-fil funnet." : "Response-fil ikke funnet.");
        sb.AppendLine();
    }

    if (requestFound)
    {
        var requestMetadata = ExtractMetadata(requestText);

        if (!string.IsNullOrWhiteSpace(requestMetadata))
        {
            sb.AppendLine("Request metadata:");
            sb.AppendLine(requestMetadata.Trim());
            sb.AppendLine();
        }

        var requestContent = ExtractContent(requestText);
        var outerFields = ExtractOuterJsonFieldsExceptData(requestContent);

        if (!string.IsNullOrWhiteSpace(outerFields))
        {
            sb.AppendLine("Ytre Content-felter:");
            sb.AppendLine(outerFields.Trim());
            sb.AppendLine();
        }
    }

    if (responseFound)
    {
        var responseMetadata = ExtractMetadata(responseText);

        if (!string.IsNullOrWhiteSpace(responseMetadata))
        {
            sb.AppendLine("Response metadata:");
            sb.AppendLine(responseMetadata.Trim());
        }
    }

    return sb.ToString().Trim();
}

static string ExtractContent(string text)
{
    var marker = "Content:";
    var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);

    if (index < 0)
        return "";

    return text.Substring(index + marker.Length).Trim();
}

static string ExtractMetadata(string text)
{
    var marker = "Content:";
    var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);

    if (index < 0)
        return text.Trim();

    return text.Substring(0, index).Trim();
}

static string ExtractInnerDataJson(string content)
{
    try
    {
        using var doc = JsonDocument.Parse(content);

        if (!doc.RootElement.TryGetProperty("Data", out var dataProperty))
            return "";

        if (dataProperty.ValueKind != JsonValueKind.String)
            return "";

        var dataString = dataProperty.GetString();

        if (string.IsNullOrWhiteSpace(dataString))
            return "";

        dataString = dataString.Trim();

        if (!(dataString.StartsWith("{") || dataString.StartsWith("[")))
            return dataString;

        return TryFormatJson(dataString);
    }
    catch
    {
        return "";
    }
}

static string ExtractOuterJsonFieldsExceptData(string content)
{
    if (string.IsNullOrWhiteSpace(content))
        return "";

    try
    {
        using var doc = JsonDocument.Parse(content);

        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return "";

        var sb = new StringBuilder();

        foreach (var property in doc.RootElement.EnumerateObject())
        {
            if (property.NameEquals("Data"))
                continue;

            sb.Append(property.Name);
            sb.Append(": ");
            sb.AppendLine(JsonValueToText(property.Value));
        }

        return sb.ToString();
    }
    catch
    {
        return "";
    }
}

static string JsonValueToText(JsonElement element)
{
    return element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? "",
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        _ => JsonSerializer.Serialize(element, GetJsonOptions())
    };
}

static string TryFormatJson(string text)
{
    if (string.IsNullOrWhiteSpace(text))
        return text;

    var trimmed = text.Trim();

    if (!(trimmed.StartsWith("{") || trimmed.StartsWith("[")))
        return text.Trim();

    try
    {
        using var doc = JsonDocument.Parse(trimmed);
        return JsonSerializer.Serialize(doc.RootElement, GetJsonOptions());
    }
    catch
    {
        return text.Trim();
    }
}

static JsonSerializerOptions GetJsonOptions()
{
    return new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}

static string HtmlPage(string title, string body)
{
    return """
<!doctype html>
<html lang="no">
<head>
<meta charset="utf-8">
<title>__TITLE__</title>
<style>
body { font-family: Arial, sans-serif; margin: 24px; color: #222; }
.box { border: 1px solid #ccc; background: #f7f7f7; padding: 12px; }
</style>
</head>
<body>
<h1>__TITLE__</h1>
<div class="box">__BODY__</div>
</body>
</html>
"""
    .Replace("__TITLE__", WebUtility.HtmlEncode(title))
    .Replace("__BODY__", body);
}

static string BuildTabbedHtml(
    string file,
    string dataText,
    string responseText,
    string metadataText,
    string defaultIisLogPath,
    string inboundLogPath,
    string outboundLogPath,
    string initialTab = "data")
{
    return """
<!doctype html>
<html lang="no">
<head>
<meta charset="utf-8">
<title>Loggvisning</title>
<style>
body {
    font-family: Arial, sans-serif;
    margin: 24px;
    color: #222;
    background: #fff;
}
h1 {
    margin-bottom: 8px;
}
.path {
    background: #f4f4f4;
    border: 1px solid #ccc;
    padding: 10px;
    margin-bottom: 18px;
    word-break: break-all;
}
.tabs {
    display: flex;
    gap: 6px;
    border-bottom: 1px solid #aaa;
    margin-bottom: 0;
}
.tab-button {
    border: 1px solid #aaa;
    border-bottom: 0;
    background: #e8e8e8;
    padding: 10px 16px;
    cursor: pointer;
    font-weight: bold;
}
.tab-button.active {
    background: #fff;
    position: relative;
    top: 1px;
}
.tab-content {
    display: none;
    border: 1px solid #aaa;
    border-top: 0;
    padding: 0;
}
.tab-content.active {
    display: block;
}
.iis-panel {
    padding: 14px;
}
.iis-row {
    display: flex;
    gap: 12px;
    align-items: center;
    margin-bottom: 14px;
}
.iis-row input[type=text] {
    flex: 1;
    min-width: 0;
    padding: 8px;
}
.iis-row select {
    flex: 1;
    min-width: 0;
    padding: 8px;
}
.iis-row button {
    padding: 8px 12px;
    cursor: pointer;
}
.iis-summary {
    padding: 14px;
}
.iis-table {
    border-collapse: collapse;
    margin: 0 0 18px;
    width: 100%;
}
.iis-table th,
.iis-table td {
    border: 1px solid #ccc;
    padding: 8px 10px;
    text-align: left;
    vertical-align: top;
}
.iis-table th {
    background: #f0f0f0;
}
.iis-table .iis-table {
    margin: 8px 0 0;
    width: 100%;
}
.iis-details {
    margin: 4px 0;
}
.iis-error-time {
    color: #c62828;
    font-weight: bold;
}
.iis-table td.number {
    text-align: right;
    white-space: nowrap;
}
.iis-section-title {
    margin: 18px 0 8px;
    font-size: 1.1rem;
}
.inbound-panel {
    padding: 14px;
}
.inbound-row {
    display: flex;
    gap: 12px;
    align-items: center;
    margin-bottom: 14px;
}
.inbound-row input,
.inbound-row select {
    padding: 8px;
}
.inbound-row input {
    width: 120px;
}
.inbound-filter-label {
    font-weight: bold;
}
.inbound-table .level {
    font-weight: bold;
    white-space: nowrap;
}
.inbound-level-error,
.inbound-level-fatal {
    color: #c62828;
}
.inbound-level-warn {
    color: #b26a00;
}
.inbound-level-info {
    color: #1769aa;
}
.inbound-message {
    white-space: pre-wrap;
    word-break: break-word;
}
.inbound-muted {
    color: #666;
}
.locks-panel {
    padding: 14px;
}
.locks-toolbar {
    display: flex;
    align-items: center;
    gap: 12px;
    margin-bottom: 14px;
}
.locks-status {
    color: #666;
}
.lock-action {
    border: 1px solid #888;
    background: #fff;
    border-radius: 4px;
    cursor: pointer;
    font-size: 1.1rem;
    line-height: 1;
    padding: 6px 9px;
}
.lock-action:hover:not(:disabled) {
    background: #e8f2ff;
}
.lock-action:disabled {
    cursor: not-allowed;
    opacity: .45;
}
.lock-error {
    max-width: 420px;
    white-space: pre-wrap;
    word-break: break-word;
}
.locks-empty {
    color: #666;
    padding: 12px 0;
}
pre {
    margin: 0;
    background: #fafafa;
    padding: 14px;
    overflow: auto;
    white-space: pre-wrap;
    word-break: break-word;
    font-family: Consolas, Menlo, Monaco, monospace;
    font-size: 13px;
    line-height: 1.35;
}
</style>
<script>
function showTab(id) {
    var buttons = document.getElementsByClassName('tab-button');
    for (var i = 0; i < buttons.length; i++) {
        buttons[i].classList.remove('active');
    }

    var contents = document.getElementsByClassName('tab-content');
    for (var j = 0; j < contents.length; j++) {
        contents[j].classList.remove('active');
    }

    document.getElementById('btn-' + id).classList.add('active');
    document.getElementById('tab-' + id).classList.add('active');
}

async function loadIisSummary() {
    var input = document.getElementById('iis-file');
    var summary = document.getElementById('iis-summary');
    var file = encodeURIComponent(input.value.trim());

    if (!file) {
        summary.innerText = 'Oppgi filsti.';
        return;
    }

    summary.innerText = 'Laster...';

    try {
        var response = await fetch('/api/iis/summary?file=' + file);
        if (!response.ok) {
            var errorText = await response.text();
            try {
                var errorJson = JSON.parse(errorText);
                summary.innerText = 'Feil ved lasting: HTTP ' + response.status + ': ' + (errorJson.detail || errorJson.error || 'Ukjent feil');
            } catch (error) {
                summary.innerText = 'Feil ved lasting: HTTP ' + response.status;
            }
            return;
        }

        var json = await response.json();
        summary.innerHTML = renderIisTables(json);
    } catch (error) {
        summary.innerText = 'Feil ved lasting av IIS-loggen.';
    }
}

async function loadIisFiles(selectPath) {
    var select = document.getElementById('iis-file');
    var summary = document.getElementById('iis-summary');
    var response = await fetch('/api/iis/files');
    if (!response.ok) {
        var errorText = await response.text();
        try {
            var errorJson = JSON.parse(errorText);
            summary.innerText = 'Klarte ikke å hente IIS-filer (HTTP ' + response.status + '): ' + (errorJson.detail || errorJson.error || 'Ukjent feil');
        } catch (error) {
            summary.innerText = 'Klarte ikke å hente IIS-filer (HTTP ' + response.status + ').';
        }
        return;
    }

    var files = await response.json();
    select.innerHTML = '';
    for (var item of files) {
        var option = document.createElement('option');
        option.value = item.path;
        option.textContent = item.file + ' (' + item.modified + ')';
        select.appendChild(option);
    }

    if (selectPath && files.some(function(item) { return item.path === selectPath; }))
        select.value = selectPath;
    else if (files.length > 0)
        select.value = files[0].path;

    if (select.value) {
        loadIisSummary();
    } else {
        summary.innerText = 'Fant ingen IIS-loggfiler i konfigurert mappe.';
    }
}

async function loadInboundFiles(selectDate) {
    var response = await fetch('/api/inbound/files');
    if (!response.ok) return;

    var files = await response.json();
    var select = document.getElementById('inbound-date');
    select.innerHTML = '';
    for (var item of files) {
        var option = document.createElement('option');
        option.value = item.date;
        option.textContent = item.file;
        select.appendChild(option);
    }

    if (selectDate && files.some(function(item) { return item.date === selectDate; }))
        select.value = selectDate;
    else if (files.length > 0)
        select.value = files[0].date;

    if (select.value) loadInboundSummary();
}

async function loadInboundSummary() {
    var date = document.getElementById('inbound-date').value;
    var summary = document.getElementById('inbound-summary');
    if (!date) {
        summary.innerHTML = '<p class="inbound-muted">Fant ingen filer med formatet yyyy-MM-dd.txt.</p>';
        return;
    }

    summary.innerText = 'Laster...';
    try {
        var response = await fetch('/api/inbound/summary?date=' + encodeURIComponent(date));
        if (!response.ok) {
            summary.innerText = 'Filen kunne ikke lastes (HTTP ' + response.status + ').';
            return;
        }
        inboundData = await response.json();
        populateInboundLevels(inboundData.entries);
        renderFilteredInbound();
    } catch (error) {
        summary.innerText = 'Feil ved lasting av Inbound-loggen.';
    }
}

var inboundData = null;

function populateInboundLevels(entries) {
    var select = document.getElementById('inbound-level');
    var selected = select.value;
    var levels = [...new Set((entries || []).map(function(entry) { return entry.level; }))].sort();
    select.innerHTML = '<option value="">Alle nivåer</option>';
    for (var level of levels) {
        select.innerHTML += '<option value="' + escapeHtml(level) + '">' + escapeHtml(level) + '</option>';
    }
    if (levels.includes(selected)) select.value = selected;
}

function renderFilteredInbound() {
    if (!inboundData) return;
    var level = document.getElementById('inbound-level').value;
    var from = document.getElementById('inbound-time-from').value;
    var to = document.getElementById('inbound-time-to').value;
    var entries = (inboundData.entries || []).filter(function(entry) {
        var time = entry.timestamp.substring(11, 19);
        return (!level || entry.level === level) && (!from || time >= from) && (!to || time <= to);
    });
    document.getElementById('inbound-summary').innerHTML = renderInboundSummary(entries);
}

var outboundData = null;

async function loadOutboundFiles(selectDate) {
    var response = await fetch('/api/outbound/files');
    if (!response.ok) return;

    var files = await response.json();
    var select = document.getElementById('outbound-date');
    select.innerHTML = '';
    for (var item of files) {
        var option = document.createElement('option');
        option.value = item.date;
        option.textContent = item.file;
        select.appendChild(option);
    }

    if (selectDate && files.some(function(item) { return item.date === selectDate; }))
        select.value = selectDate;
    else if (files.length > 0)
        select.value = files[0].date;

    if (select.value) loadOutboundSummary();
}

async function loadOutboundSummary() {
    var date = document.getElementById('outbound-date').value;
    var summary = document.getElementById('outbound-summary');
    if (!date) {
        summary.innerHTML = '<p class="inbound-muted">Fant ingen filer med formatet yyyy-MM-dd.txt.</p>';
        return;
    }

    summary.innerText = 'Laster...';
    try {
        var response = await fetch('/api/outbound/summary?date=' + encodeURIComponent(date));
        if (!response.ok) {
            summary.innerText = 'Filen kunne ikke lastes (HTTP ' + response.status + ').';
            return;
        }
        outboundData = await response.json();
        populateLogLevels('outbound-level', outboundData.entries);
        renderFilteredOutbound();
    } catch (error) {
        summary.innerText = 'Feil ved lasting av OutBound-loggen.';
    }
}

function renderFilteredOutbound() {
    if (!outboundData) return;
    var level = document.getElementById('outbound-level').value;
    var from = document.getElementById('outbound-time-from').value;
    var to = document.getElementById('outbound-time-to').value;
    var entries = (outboundData.entries || []).filter(function(entry) {
        var time = entry.timestamp.substring(11, 19);
        return (!level || entry.level === level) && (!from || time >= from) && (!to || time <= to);
    });
    document.getElementById('outbound-summary').innerHTML = renderInboundSummary(entries);
}

var locksData = [];

async function loadLocks() {
    var summary = document.getElementById('locks-summary');
    summary.innerText = 'Laster låser...';
    try {
        var response = await fetch('/api/locks');
        var result = await response.json();
        if (!response.ok) {
            summary.innerText = result.error || 'Klarte ikke å hente låser.';
            return;
        }
        locksData = result;
        renderLocks();
    } catch (error) {
        summary.innerText = 'Feil ved lasting av låser.';
    }
}

function renderLocks() {
    var summary = document.getElementById('locks-summary');
    if (!locksData.length) {
        summary.innerHTML = '<div class="locks-empty">Fant ingen aktive låser.</div>';
        return;
    }

    var html = '<table class="iis-table"><thead><tr><th>Type</th><th>Installasjon</th><th>Lås-ID</th><th>Feilmelding</th><th>Handling</th></tr></thead><tbody>';
    for (var lock of locksData) {
        var canUnlock = lock.typeOfLock !== 'Assignment';
        var title = canUnlock ? 'Lås opp' : 'Assignment-låser har ikke unlock-endepunkt';
        html += '<tr><td>' + escapeHtml(lock.typeOfLock) + '</td>';
        html += '<td>' + escapeHtml(lock.installationId) + '</td>';
        html += '<td>' + escapeHtml(lock.lockId) + '</td>';
        html += '<td class="lock-error">' + escapeHtml(lock.errorMessage || '') + '</td><td>';
        html += '<button class="lock-action" title="' + escapeHtml(title) + '" aria-label="' + escapeHtml(title) + '"';
        html += ' data-installation-id="' + escapeHtml(lock.installationId) + '" data-lock-id="' + escapeHtml(lock.lockId) + '" data-type-of-lock="' + escapeHtml(lock.typeOfLock) + '"';
        if (!canUnlock) html += ' disabled';
        html += ' onclick="unlockLock(this)">&#128274;</button></td></tr>';
    }
    summary.innerHTML = html + '</tbody></table>';
}

async function unlockLock(button) {
    var lock = {
        installationId: button.dataset.installationId,
        lockId: button.dataset.lockId,
        typeOfLock: button.dataset.typeOfLock
    };
    if (!confirm('Låse opp ' + lock.typeOfLock + ' ' + lock.lockId + '?')) return;

    button.disabled = true;
    try {
        var response = await fetch('/api/locks/unlock', {
            method: 'POST',
            headers: {'Content-Type': 'application/json'},
            body: JSON.stringify(lock)
        });
        var result = await response.json();
        if (!response.ok) {
            alert(result.error || 'Klarte ikke å låse opp posten.');
            button.disabled = false;
            return;
        }
        await loadLocks();
    } catch (error) {
        alert('Feil ved unlock-kallet.');
        button.disabled = false;
    }
}

function populateLogLevels(selectId, entries) {
    var select = document.getElementById(selectId);
    var selected = select.value;
    var levels = [...new Set((entries || []).map(function(entry) { return entry.level; }))].sort();
    select.innerHTML = '<option value="">Alle nivåer</option>';
    for (var level of levels)
        select.innerHTML += '<option value="' + escapeHtml(level) + '">' + escapeHtml(level) + '</option>';
    if (levels.includes(selected)) select.value = selected;
}

function renderInboundSummary(entries) {
    var levelCounts = {};
    for (var entry of entries) levelCounts[entry.level] = (levelCounts[entry.level] || 0) + 1;
    var html = '<div class="inbound-stats">';
    html += '<b>Hendelser:</b> ' + entries.length;
    html += ' &nbsp; <b>Første:</b> ' + escapeHtml(entries[0]?.timestamp || '-');
    html += ' &nbsp; <b>Siste:</b> ' + escapeHtml(entries[entries.length - 1]?.timestamp || '-');
    html += '</div>';
    html += renderCountTable('Nivåer', levelCounts);
    html += '<h3 class="iis-section-title">Hendelser</h3>';
    html += '<table class="iis-table inbound-table"><thead><tr><th>Tid</th><th>Nivå</th><th>Melding</th></tr></thead><tbody>';
    for (var entry of entries) {
        var level = String(entry.level || '').toLowerCase();
        var levelClass = 'inbound-level-' + level;
        html += '<tr><td>' + escapeHtml(entry.timestamp) + '</td>';
        html += '<td class="level ' + escapeHtml(levelClass) + '">' + escapeHtml(entry.level) + '</td>';
        html += '<td><details><summary>' + escapeHtml(entry.summary) + '</summary>';
        html += '<div class="inbound-message">' + escapeHtml(entry.message) + '</div></details></td></tr>';
    }
    return html + '</tbody></table>';
}

function renderIisTables(json) {
    var html = '<h3 class="iis-section-title">Oversikt</h3>';
    html += '<table class="iis-table"><thead><tr><th>Element</th><th>Antall</th></tr></thead><tbody>';
    html += '<tr><td>Totale forespørsler</td><td class="number">' + json.totalRequests + '</td></tr>';
    html += '</tbody></table>';

    html += renderStatusDrilldown('Statuskoder', json.statusDetails);
    html += '<h3 class="iis-section-title">Topp paths</h3>';
    html += '<table class="iis-table"><thead><tr><th>Path</th><th>Antall</th><th>Statuskoder og per time</th></tr></thead><tbody>';
    for (var path of (json.topPaths || [])) {
        html += '<tr><td>' + escapeHtml(path.path) + '</td>';
        html += '<td class="number">' + path.count + '</td><td>';
        html += '<details><summary>Vis detaljer</summary>';
        html += '<h4>Statuskoder</h4>' + renderCountTable('', path.statusCounts);
        html += renderHourlyTable('Antall per time', path.requestsPerHour);
        html += '</details>';
        html += '</td></tr>';
    }
    html += '</tbody></table>';

    html += '<h3 class="iis-section-title">Forespørsler per time</h3>';
    html += renderHourlyTable('', json.requestsPerHour);
    return html;
}

function renderCountTable(title, values) {
    var html = title ? '<h3 class="iis-section-title">' + title + '</h3>' : '';
    html += '<table class="iis-table"><thead><tr><th>Navn</th><th>Antall</th></tr></thead><tbody>';
    for (var value of Object.entries(values || {})) {
        html += '<tr><td>' + escapeHtml(value[0]) + '</td><td class="number">' + value[1] + '</td></tr>';
    }
    return html + '</tbody></table>';
}

function renderStatusDrilldown(title, values) {
    var html = title ? '<h3 class="iis-section-title">' + title + '</h3>' : '';
    html += '<table class="iis-table"><thead><tr><th>Statuskode</th><th>Antall</th></tr></thead><tbody>';
    for (var status of (values || [])) {
        html += '<tr><td colspan="2"><details class="iis-details"><summary>';
        html += escapeHtml(status.status) + ' (' + status.count + ')</summary>';
        html += renderStatusHours(status.requestsPerHour);
        html += '</details></td></tr>';
    }
    return html + '</tbody></table>';
}

function renderStatusHours(values) {
    var html = '<table class="iis-table"><thead><tr><th>Time</th><th>Antall</th><th>Paths</th></tr></thead><tbody>';
    for (var hour of (values || [])) {
        html += '<tr><td>' + escapeHtml(hour.hour) + '</td>';
        html += '<td class="number">' + hour.count + '</td><td>';
        html += '<table class="iis-table"><tbody>';
        for (var path of (hour.paths || [])) {
            html += '<tr><td>' + escapeHtml(path.path) + '</td><td class="number">' + path.count + '</td></tr>';
        }
        html += '</tbody></table></td></tr>';
    }
    return html + '</tbody></table>';
}

function renderHourlyTable(title, values) {
    var html = title ? '<h4>' + title + '</h4>' : '';
    html += '<table class="iis-table"><thead><tr><th>Time</th><th>Antall</th><th>Statuskoder</th></tr></thead><tbody>';
    for (var hour of (values || [])) {
        var hasError = Object.keys(hour.statusCounts || {}).some(function(status) {
            return /^(4|5)\d\d;/.test(status);
        });
        var timeClass = hasError ? ' class="iis-error-time"' : '';
        html += '<tr><td colspan="3"><details class="iis-details"><summary>';
        html += '<span' + timeClass + '>' + escapeHtml(hour.hour) + '</span> (' + hour.count + ')</summary>';
        html += renderCountTable('', hour.statusCounts);
        html += '</details></td></tr>';
    }
    return html + '</tbody></table>';
}

function escapeHtml(value) {
    return String(value ?? '').replace(/[&<>'"]/g, function(character) {
        return {'&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;'}[character];
    });
}
</script>
</head>
<body onload="showTab('__INITIAL_TAB__'); if ('__INITIAL_TAB__' === 'iis') loadIisFiles(); if ('__INITIAL_TAB__' === 'inbound') loadInboundFiles(); if ('__INITIAL_TAB__' === 'outbound') loadOutboundFiles(); if ('__INITIAL_TAB__' === 'locks') loadLocks()">
<h1>Loggvisning</h1>
<div class="path"><b>Fil:</b> __FILE__</div>

<div class="tabs">
    <button id="btn-data" class="tab-button active" onclick="showTab('data')">Data</button>
    <button id="btn-response" class="tab-button" onclick="showTab('response')">Respons</button>
    <button id="btn-metadata" class="tab-button" onclick="showTab('metadata')">Metadata</button>
    <button id="btn-iis" class="tab-button" onclick="showTab('iis'); loadIisFiles()">IIS</button>
    <button id="btn-inbound" class="tab-button" onclick="showTab('inbound'); loadInboundFiles('__INBOUND_DATE__')">Inbound</button>
    <button id="btn-outbound" class="tab-button" onclick="showTab('outbound'); loadOutboundFiles('__INBOUND_DATE__')">OutBound</button>
    <button id="btn-locks" class="tab-button" onclick="showTab('locks'); loadLocks()">Låser</button>
</div>

<div id="tab-data" class="tab-content active">
    <pre>__DATA__</pre>
</div>

<div id="tab-response" class="tab-content">
    <pre>__RESPONSE__</pre>
</div>

<div id="tab-metadata" class="tab-content">
    <pre>__METADATA__</pre>
</div>

<div id="tab-iis" class="tab-content">
    <div class="iis-panel">
        <div class="iis-row">
            <select id="iis-file"></select>
            <button onclick="loadIisFiles(document.getElementById('iis-file').value)">Oppdater</button>
            <button onclick="loadIisSummary()">Last</button>
        </div>
        <div id="iis-summary" class="iis-summary">Trykk «Last» for å hente IIS-oppsummeringen.</div>
    </div>
</div>

<div id="tab-inbound" class="tab-content">
    <div class="inbound-panel">
        <div class="inbound-row">
            <label for="inbound-date">Loggfil:</label>
            <select id="inbound-date" onchange="loadInboundSummary()"></select>
            <button onclick="loadInboundFiles(document.getElementById('inbound-date').value)">Oppdater</button>
        </div>
        <div class="inbound-row">
            <span class="inbound-filter-label">Filtrer:</span>
            <label for="inbound-level">Nivå</label>
            <select id="inbound-level" onchange="renderFilteredInbound()">
                <option value="">Alle nivåer</option>
            </select>
            <label for="inbound-time-from">Tid fra</label>
            <input id="inbound-time-from" type="time" step="1" onchange="renderFilteredInbound()" />
            <label for="inbound-time-to">Tid til</label>
            <input id="inbound-time-to" type="time" step="1" onchange="renderFilteredInbound()" />
            <button onclick="document.getElementById('inbound-level').value=''; document.getElementById('inbound-time-from').value=''; document.getElementById('inbound-time-to').value=''; renderFilteredInbound()">Nullstill</button>
        </div>
        <div id="inbound-summary" class="iis-summary">Velg en dato for å lese Inbound-loggen.</div>
    </div>
</div>

<div id="tab-outbound" class="tab-content">
    <div class="inbound-panel">
        <div class="inbound-row">
            <label for="outbound-date">Loggfil:</label>
            <select id="outbound-date" onchange="loadOutboundSummary()"></select>
            <button onclick="loadOutboundFiles(document.getElementById('outbound-date').value)">Oppdater</button>
        </div>
        <div class="inbound-row">
            <span class="inbound-filter-label">Filtrer:</span>
            <label for="outbound-level">Nivå</label>
            <select id="outbound-level" onchange="renderFilteredOutbound()"><option value="">Alle nivåer</option></select>
            <label for="outbound-time-from">Tid fra</label>
            <input id="outbound-time-from" type="time" step="1" onchange="renderFilteredOutbound()" />
            <label for="outbound-time-to">Tid til</label>
            <input id="outbound-time-to" type="time" step="1" onchange="renderFilteredOutbound()" />
            <button onclick="document.getElementById('outbound-level').value=''; document.getElementById('outbound-time-from').value=''; document.getElementById('outbound-time-to').value=''; renderFilteredOutbound()">Nullstill</button>
        </div>
        <div id="outbound-summary" class="iis-summary">Velg en dato for å lese OutBound-loggen.</div>
    </div>
</div>

<div id="tab-locks" class="tab-content">
    <div class="locks-panel">
        <div class="locks-toolbar">
            <button onclick="loadLocks()">Oppdater</button>
            <span class="locks-status">Låser fra alle aktive installasjoner</span>
        </div>
        <div id="locks-summary" class="iis-summary">Trykk «Oppdater» for å hente låser.</div>
    </div>
</div>

</body>
</html>
"""
    .Replace("__FILE__", WebUtility.HtmlEncode(file))
    .Replace("__DATA__", WebUtility.HtmlEncode(dataText))
    .Replace("__RESPONSE__", WebUtility.HtmlEncode(responseText))
    .Replace("__METADATA__", WebUtility.HtmlEncode(metadataText))
    .Replace("__IIS_PATH__", WebUtility.HtmlEncode(defaultIisLogPath))
    .Replace("__INBOUND_DATE__", WebUtility.HtmlEncode(DateTime.Today.ToString("yyyy-MM-dd")))
    .Replace("__INITIAL_TAB__", WebUtility.HtmlEncode(initialTab));
}

 

// --- IIS parsing and metrics ---
static async Task<List<InboundEntry>> ParseInboundLogFile(string file)
{
    var entries = new List<InboundEntry>();
    InboundEntry? current = null;
    var pattern = new System.Text.RegularExpressions.Regex(
        "^(?<timestamp>\\d{4}-\\d{2}-\\d{2} \\d{2}:\\d{2}:\\d{2}\\.\\d+)\\s+(?<level>[A-Z]+)\\s+(?<message>.*)$",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    await using var stream = new FileStream(
        file,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete,
        bufferSize: 64 * 1024,
        useAsync: true);
    using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

    while (await reader.ReadLineAsync() is { } line)
    {
        var match = pattern.Match(line);
        if (match.Success && DateTime.TryParse(match.Groups["timestamp"].Value, out var timestamp))
        {
            if (current != null)
                entries.Add(current);

            current = new InboundEntry(timestamp, match.Groups["level"].Value, match.Groups["message"].Value);
        }
        else if (current != null)
        {
            current = current with { Message = current.Message + Environment.NewLine + line };
        }
    }

    if (current != null)
        entries.Add(current);

    return entries;
}

static object ComputeInboundMetrics(List<InboundEntry> entries)
{
    var ordered = entries.OrderBy(x => x.Timestamp).ToList();
    return new
    {
        totalEntries = ordered.Count,
        firstTimestamp = ordered.FirstOrDefault()?.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.ffff"),
        lastTimestamp = ordered.LastOrDefault()?.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.ffff"),
        levelCounts = ordered.GroupBy(x => x.Level).OrderByDescending(g => g.Count()).ToDictionary(g => g.Key, g => g.Count()),
        entries = ordered.Select(x => new
        {
            timestamp = x.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.ffff"),
            level = x.Level,
            summary = x.Message.Split(Environment.NewLine, 2)[0],
            message = x.Message
        }).ToList()
    };
}

static async Task<List<IisEntry>> ParseIisLogFile(string file)
{
    List<string>? fields = null;
    var entries = new List<IisEntry>();

    await using var stream = new FileStream(
        file,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete,
        bufferSize: 64 * 1024,
        useAsync: true);
    using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

    while (await reader.ReadLineAsync() is { } raw)
    {
        var line = raw.TrimEnd('\r');
        if (string.IsNullOrWhiteSpace(line))
            continue;

        if (line.StartsWith("#Fields:", StringComparison.OrdinalIgnoreCase))
        {
            var rest = line.Substring(8).Trim();
            fields = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            continue;
        }

        if (line.StartsWith('#'))
            continue;

        if (fields == null)
            continue;

        var parts = line.Split(' ', StringSplitOptions.None);
        if (parts.Length < fields.Count)
            continue;

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < Math.Min(parts.Length, fields.Count); i++)
            map[fields[i]] = parts[i];

        string Get(string name) => map.TryGetValue(name, out var v) ? v : "";

        DateTime timestamp = DateTime.MinValue;
        var date = Get("date");
        var time = Get("time");
        if (!string.IsNullOrWhiteSpace(date) && !string.IsNullOrWhiteSpace(time))
        {
            if (!DateTime.TryParseExact(date + " " + time, "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out timestamp))
                DateTime.TryParse(date + " " + time, out timestamp);
        }

        var method = Get("cs-method");
        var uriStem = Get("cs-uri-stem");
        var uriQuery = Get("cs-uri-query");
        var clientIp = Get("c-ip");
        var status = 0; int.TryParse(Get("sc-status"), out status);
        var bytes = 0L; long.TryParse(Get("sc-bytes"), out bytes);
        var timeTaken = 0; int.TryParse(Get("time-taken"), out timeTaken);

        entries.Add(new IisEntry(timestamp == DateTime.MinValue ? DateTime.Now : timestamp, method, uriStem, uriQuery, clientIp, status, bytes, timeTaken));
    }

    return entries;
}

static object ComputeIisMetrics(List<IisEntry> entries)
{
    var total = entries.Count;
    var statusCounts = entries.GroupBy(x => x.Status).OrderByDescending(g => g.Count()).ToDictionary(g => FormatStatus(g.Key), g => g.Count());
    var statusDetails = entries.GroupBy(x => x.Status).OrderByDescending(g => g.Count()).Select(status => new
    {
        status = FormatStatus(status.Key),
        count = status.Count(),
        requestsPerHour = status.GroupBy(x => x.Timestamp.ToString("yyyy-MM-dd HH:00")).OrderBy(hour => hour.Key).Select(hour => new
        {
            hour = hour.Key,
            count = hour.Count(),
            paths = hour.GroupBy(x => x.UriStem).OrderByDescending(path => path.Count()).Select(path => new
            {
                path = path.Key,
                count = path.Count()
            }).ToList()
        }).ToList()
    }).ToList();
    var topPaths = entries.GroupBy(x => x.UriStem).OrderByDescending(g => g.Count()).Take(10).Select(g => new
    {
        path = g.Key,
        count = g.Count(),
        statusCounts = g.GroupBy(x => x.Status).OrderByDescending(status => status.Count()).ToDictionary(status => FormatStatus(status.Key), status => status.Count()),
        requestsPerHour = g.GroupBy(x => x.Timestamp.ToString("yyyy-MM-dd HH:00")).OrderBy(hour => hour.Key).Select(hour => new
        {
            hour = hour.Key,
            count = hour.Count(),
            statusCounts = hour.GroupBy(x => x.Status).OrderBy(status => status.Key).ToDictionary(status => FormatStatus(status.Key), status => status.Count())
        }).ToList()
    }).ToList();
    var perHour = entries.GroupBy(x => x.Timestamp.ToString("yyyy-MM-dd HH:00")).OrderBy(hour => hour.Key).Select(hour => new
    {
        hour = hour.Key,
        count = hour.Count(),
        statusCounts = hour.GroupBy(x => x.Status).OrderBy(status => status.Key).ToDictionary(status => FormatStatus(status.Key), status => status.Count())
    }).ToList();

    return new
    {
        totalRequests = total,
        statusCounts = statusCounts,
        statusDetails = statusDetails,
        topPaths = topPaths,
        requestsPerHour = perHour
    };
}

static string FormatStatus(int status)
{
    var name = Enum.IsDefined(typeof(HttpStatusCode), status)
        ? ((HttpStatusCode)status).ToString()
        : "Ukjent";

    return $"{status}; {name}";
}
