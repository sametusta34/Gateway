using Microsoft.AspNetCore.Http;
using Polly.CircuitBreaker;
using System.Net.Http;
using System.Text.Json;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using Microsoft.Extensions.Logging;

namespace Gateway.API.Middleware;

public class GatewayMiddleware : IMiddleware
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly GatewayConfiguration _gatewayConfig;
    private readonly ILogger<GatewayMiddleware> _logger;

    public GatewayMiddleware(
        IHttpClientFactory httpClientFactory,
        GatewayConfiguration gatewayConfig,
        ILogger<GatewayMiddleware> logger)
    {
        _httpClientFactory = httpClientFactory;
        _gatewayConfig = gatewayConfig;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var path = context.Request.Path.Value;
        _logger.LogInformation("Processing request for path: {Path}", path);

        var (service, endpoint) = FindServiceAndEndpoint(path);

        if (service == null || endpoint == null)
        {
            _logger.LogWarning("Endpoint not found for path: {Path}", path);
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(new { message = "Endpoint not found" });
            return;
        }

        if (!path.EndsWith("/users/auth/login"))
        {
            var authHeader = context.Request.Headers["Authorization"].FirstOrDefault();
            if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer "))
            {
                _logger.LogWarning("Missing or invalid authorization header");
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { message = "Token required" });
                return;
            }
        }

        await HandleRequest(context, service, endpoint);
    }

    private async Task HandleRequest(HttpContext context, ServiceConfiguration service, EndpointConfiguration endpoint)
    {
        if (!endpoint.AllowedMethods.Contains(context.Request.Method.ToUpper()))
        {
            _logger.LogWarning("Method not allowed: {Method} for service {Service}", 
                context.Request.Method, service.Name);
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            await context.Response.WriteAsJsonAsync(new { message = "Method not allowed" });
            return;
        }

        var client = _httpClientFactory.CreateClient("GatewayClient");
        var targetUri = BuildTargetUrl(context.Request, service, endpoint);
        
        _logger.LogInformation("Forwarding request to: {TargetUri}", targetUri);

        try
        {
            var requestMessage = await CreateRequestMessage(context, targetUri);
            var response = await client.SendAsync(requestMessage);

            _logger.LogInformation("Response received from {Service}: {StatusCode}", 
                service.Name, response.StatusCode);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                var content = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Unauthorized response from {Service}: {Content}", 
                    service.Name, content);
            }

            await CopyResponseToContext(response, context);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Request failed for {Service}", service.Name);
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            await context.Response.WriteAsJsonAsync(new { 
                message = $"Gateway Error: {ex.Message}",
                details = ex.InnerException?.Message,
                service = service.Name,
                targetUri = targetUri
            });
        }
    }

    private async Task<HttpRequestMessage> CreateRequestMessage(HttpContext context, string targetUri)
    {
        var requestMessage = new HttpRequestMessage();
        
        // Authorization header'ı varsa, onu da ilet
        var authHeader = context.Request.Headers["Authorization"].FirstOrDefault();
        if (!string.IsNullOrEmpty(authHeader))
        {
            Console.WriteLine($"Forwarding Authorization header: {authHeader}"); // Debug için
            requestMessage.Headers.TryAddWithoutValidation("Authorization", authHeader);
        }

        var requestMethod = context.Request.Method;
        
        if (!HttpMethods.IsGet(requestMethod) &&
            !HttpMethods.IsHead(requestMethod) &&
            !HttpMethods.IsDelete(requestMethod) &&
            !HttpMethods.IsTrace(requestMethod))
        {
            // Request body'yi oku
            var bodyContent = await new StreamReader(context.Request.Body).ReadToEndAsync();
            
            // Content-Type'ı al
            var contentType = context.Request.Headers.ContainsKey("Content-Type")
                ? context.Request.Headers["Content-Type"].ToString()
                : "application/json";

            // StringContent oluştur ve encoding belirt
            var content = new StringContent(bodyContent, System.Text.Encoding.UTF8, contentType);
            requestMessage.Content = content;
        }

        // Transfer-Encoding header'ını kaldır
        if (context.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            context.Request.Headers.Remove("Transfer-Encoding");
        }

        foreach (var header in context.Request.Headers)
        {
            if (!header.Key.ToLower().StartsWith("host") && 
                !header.Key.ToLower().StartsWith("content-") &&
                !header.Key.ToLower().StartsWith("transfer-encoding") &&
                !header.Key.ToLower().StartsWith("authorization")) // Authorization header'ı iki kere eklememek için
            {
                requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }

        requestMessage.Method = new HttpMethod(requestMethod);
        requestMessage.RequestUri = new Uri(targetUri);

        Console.WriteLine($"Request URI: {targetUri}");
        Console.WriteLine($"Request Headers: {string.Join(", ", requestMessage.Headers)}");

        return requestMessage;
    }

    private async Task CopyResponseToContext(HttpResponseMessage response, HttpContext context)
    {
        Console.WriteLine($"Response Status: {response.StatusCode}"); // Debug için
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            var content = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"Unauthorized Response Content: {content}"); // Debug için
        }

        context.Response.StatusCode = (int)response.StatusCode;

        // Transfer-Encoding header'ını kaldır
        if (response.Headers.Contains("Transfer-Encoding"))
        {
            response.Headers.Remove("Transfer-Encoding");
        }

        foreach (var header in response.Headers)
        {
            if (!header.Key.ToLower().StartsWith("transfer-encoding"))
            {
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }
        }

        if (response.Content != null)
        {
            // Content-Length header'ını ayarla
            var content = await response.Content.ReadAsStringAsync();
            var contentBytes = System.Text.Encoding.UTF8.GetBytes(content);
            context.Response.ContentLength = contentBytes.Length;

            // Content-Type header'ını ayarla
            context.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";

            // İçeriği yaz
            await context.Response.Body.WriteAsync(contentBytes, 0, contentBytes.Length);
        }
    }

    private (ServiceConfiguration service, EndpointConfiguration endpoint) FindServiceAndEndpoint(string path)
    {
        foreach (var service in _gatewayConfig.Services)
        {
            if (!path.StartsWith($"/{service.Path}/", StringComparison.OrdinalIgnoreCase))
                continue;

            var remainingPath = path.Substring($"/{service.Path}/".Length);

            // 1. Önce spesifik endpoint'leri kontrol et
            var exactMatch = service.Endpoints.FirstOrDefault(e => 
                !e.Path.Contains("*") && 
                remainingPath.Equals(e.Path, StringComparison.OrdinalIgnoreCase));

            if (exactMatch != null)
            {
                _logger.LogInformation("Exact match found: {Path}", path);
                return (service, exactMatch);
            }

            // 2. Spesifik eşleşme yoksa wildcard endpoint'leri kontrol et
            foreach (var endpoint in service.Endpoints.Where(e => e.Path.Contains("*")))
            {
                var wildcardBase = endpoint.Path.TrimEnd('*', '/');
                var isDeepWildcard = endpoint.Path.EndsWith("/**");

                if (remainingPath.StartsWith(wildcardBase, StringComparison.OrdinalIgnoreCase))
                {
                    var remainingSegments = remainingPath.Substring(wildcardBase.Length)
                        .Trim('/').Split('/');

                    // /** için tüm alt path'leri kabul et
                    if (isDeepWildcard || remainingSegments.Length <= 1)
                    {
                        _logger.LogInformation("Wildcard match found: {Path} -> {EndpointPath}", 
                            path, endpoint.Path);
                        return (service, endpoint);
                    }
                }
            }
        }

        return (null, null);
    }

    private string BuildTargetUrl(HttpRequest request, ServiceConfiguration service, EndpointConfiguration endpoint)
    {
        var originalPath = request.Path.Value;
        var servicePrefixLength = $"/{service.Path}/".Length;
        var remainingPath = originalPath.Substring(servicePrefixLength);

        string targetPath;
        if (endpoint.Path.Contains("*"))
        {
            // Wildcard endpoint için path'i koru
            var wildcardBase = endpoint.Path.TrimEnd('*', '/');
            var specificPath = remainingPath.Substring(wildcardBase.Length).TrimStart('/');
            targetPath = $"{endpoint.RelativePath.TrimEnd('/')}/{specificPath}";
        }
        else
        {
            // Spesifik endpoint için RelativePath'i kullan
            targetPath = endpoint.RelativePath;
        }

        var queryString = request.QueryString.Value;
        return $"{service.BaseUrl.TrimEnd('/')}/{targetPath.TrimStart('/')}{queryString}";
    }
} 