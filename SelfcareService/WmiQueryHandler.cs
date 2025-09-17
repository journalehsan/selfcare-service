#if WINDOWS
using System.Management;
#endif
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SelfcareService;

/// <summary>
/// Handles WMI (Windows Management Instrumentation) queries for the service
/// </summary>
public class WmiQueryHandler
{
    private readonly ILogger<WmiQueryHandler> _logger;

    public WmiQueryHandler(ILogger<WmiQueryHandler> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Process a WMI query request with headers
    /// </summary>
    public async Task<WmiQueryResponse> ProcessWmiQuery(string query, Dictionary<string, string> headers)
    {
        try
        {
            // Check if running on Windows
            if (!OperatingSystem.IsWindows())
            {
                return new WmiQueryResponse
                {
                    Success = false,
                    Error = "WMI queries are only supported on Windows",
                    Data = null,
                    Format = headers.GetValueOrDefault("format", "text")
                };
            }

            var format = headers.GetValueOrDefault("format", "text").ToLower();
            var timeout = int.Parse(headers.GetValueOrDefault("timeout", "30"));
            var maxResults = int.Parse(headers.GetValueOrDefault("max_results", "1000"));

            _logger.LogInformation($"Executing WMI query: {query} (format: {format}, timeout: {timeout}s)");

            // Execute WMI query
            var results = await ExecuteWmiQuery(query, timeout, maxResults);

            // Format results based on requested format
            string formattedData = format switch
            {
                "json" => FormatAsJson(results),
                "csv" => FormatAsCsv(results),
                _ => FormatAsText(results)
            };

            return new WmiQueryResponse
            {
                Success = true,
                Error = null,
                Data = formattedData,
                Format = format,
                ResultCount = results.Count,
                QueryTime = DateTime.UtcNow
            };
        }
#if WINDOWS
        catch (ManagementException ex)
        {
            _logger.LogError(ex, $"WMI query failed: {query}");
            return new WmiQueryResponse
            {
                Success = false,
                Error = $"WMI Error: {ex.Message} (Code: {ex.ErrorCode})",
                Data = null,
                Format = headers.GetValueOrDefault("format", "text")
            };
        }
#endif
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Unexpected error executing WMI query: {query}");
            return new WmiQueryResponse
            {
                Success = false,
                Error = $"Unexpected error: {ex.Message}",
                Data = null,
                Format = headers.GetValueOrDefault("format", "text")
            };
        }
    }

    private async Task<List<Dictionary<string, object>>> ExecuteWmiQuery(string query, int timeoutSeconds, int maxResults)
    {
        var results = new List<Dictionary<string, object>>();

#if WINDOWS
        return await Task.Run(() =>
        {
            using var searcher = new ManagementObjectSearcher(query);
            searcher.Options.Timeout = TimeSpan.FromSeconds(timeoutSeconds);

            int count = 0;
            foreach (ManagementObject obj in searcher.Get())
            {
                if (count >= maxResults) break;

                var item = new Dictionary<string, object>();
                foreach (PropertyData prop in obj.Properties)
                {
                    try
                    {
                        var value = prop.Value;

                        // Handle special data types
                        if (value is ManagementBaseObject[] array)
                        {
                            value = array.Select(o => o?.ToString()).ToArray();
                        }
                        else if (value is ManagementBaseObject mbo)
                        {
                            value = mbo.ToString();
                        }
                        else if (value is byte[] bytes)
                        {
                            value = Convert.ToBase64String(bytes);
                        }
                        else if (value is DateTime dt)
                        {
                            value = dt.ToString("yyyy-MM-dd HH:mm:ss");
                        }

                        item[prop.Name] = value ?? "null";
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"Failed to read property {prop.Name}: {ex.Message}");
                        item[prop.Name] = "error";
                    }
                }
                results.Add(item);
                count++;
            }

            return results;
        });
#else
        return await Task.FromResult(results);
#endif
    }

    private string FormatAsJson(List<Dictionary<string, object>> results)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        return JsonSerializer.Serialize(results, options);
    }

    private string FormatAsText(List<Dictionary<string, object>> results)
    {
        var sb = new StringBuilder();

        foreach (var item in results)
        {
            sb.AppendLine(new string('-', 50));
            foreach (var kvp in item)
            {
                sb.AppendLine($"{kvp.Key}: {kvp.Value}");
            }
        }

        if (results.Count == 0)
        {
            sb.AppendLine("No results found.");
        }

        return sb.ToString();
    }

    private string FormatAsCsv(List<Dictionary<string, object>> results)
    {
        if (results.Count == 0) return "No results";

        var sb = new StringBuilder();

        // Header
        var headers = results.First().Keys;
        sb.AppendLine(string.Join(",", headers.Select(h => $"\"{h}\"")));

        // Data rows
        foreach (var item in results)
        {
            var values = headers.Select(h =>
            {
                var value = item.GetValueOrDefault(h, "")?.ToString() ?? "";
                // Escape CSV special characters
                if (value.Contains(",") || value.Contains("\"") || value.Contains("\n"))
                {
                    value = $"\"{value.Replace("\"", "\"\"")}\"";
                }
                return value;
            });
            sb.AppendLine(string.Join(",", values));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Validate if a WMI query is safe to execute
    /// </summary>
    public bool IsQuerySafe(string query)
    {
        // Basic validation - can be extended
        var dangerousKeywords = new[] { "DELETE", "UPDATE", "INSERT", "DROP", "CREATE", "ALTER" };
        var upperQuery = query.ToUpper();

        return !dangerousKeywords.Any(keyword => upperQuery.Contains(keyword));
    }

    /// <summary>
    /// Get domain information for the current computer
    /// </summary>
    public async Task<WmiQueryResponse> GetDomainInfo()
    {
        var query = "SELECT Domain, PartOfDomain, Name FROM Win32_ComputerSystem";
        var headers = new Dictionary<string, string> { ["format"] = "json" };
        return await ProcessWmiQuery(query, headers);
    }

    /// <summary>
    /// Check if computer is joined to a specific domain
    /// </summary>
    public async Task<bool> IsJoinedToDomain(string domainName)
    {
        try
        {
            var domainInfo = await GetDomainInfo();
            if (!domainInfo.Success || string.IsNullOrEmpty(domainInfo.Data))
            {
                return false;
            }

            var results = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(domainInfo.Data);
            if (results?.Count > 0)
            {
                var computerInfo = results[0];
                if (computerInfo.TryGetValue("domain", out var domain) &&
                    computerInfo.TryGetValue("partOfDomain", out var partOfDomain))
                {
                    var domainStr = domain?.ToString() ?? "";
                    var isPartOfDomain = partOfDomain is JsonElement element && element.GetBoolean();

                    return isPartOfDomain && domainStr.ToLower().Contains(domainName.ToLower());
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error checking domain membership for {domainName}");
        }

        return false;
    }

    /// <summary>
    /// Get common WMI query templates
    /// </summary>
    public static Dictionary<string, string> GetCommonQueries()
    {
        return new Dictionary<string, string>
        {
            ["services"] = "SELECT Name, State, StartMode FROM Win32_Service",
            ["processes"] = "SELECT Name, ProcessId, WorkingSetSize, CommandLine FROM Win32_Process",
            ["system"] = "SELECT * FROM Win32_ComputerSystem",
            ["os"] = "SELECT * FROM Win32_OperatingSystem",
            ["network"] = "SELECT * FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = true",
            ["disk"] = "SELECT * FROM Win32_LogicalDisk WHERE DriveType = 3",
            ["startup"] = "SELECT * FROM Win32_StartupCommand",
            ["hotfix"] = "SELECT * FROM Win32_QuickFixEngineering",
            ["users"] = "SELECT * FROM Win32_UserAccount WHERE LocalAccount = true",
            ["shares"] = "SELECT * FROM Win32_Share",
            ["domain"] = "SELECT Domain, PartOfDomain, Name FROM Win32_ComputerSystem"
        };
    }
}

/// <summary>
/// Response structure for WMI queries
/// </summary>
public class WmiQueryResponse
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? Data { get; set; }
    public string Format { get; set; } = "text";
    public int? ResultCount { get; set; }
    public DateTime? QueryTime { get; set; }
}

/// <summary>
/// Request structure for WMI queries with headers
/// </summary>
public class WmiQueryRequest
{
    public Dictionary<string, string> Headers { get; set; } = new();
    public string Query { get; set; } = "";

    public static WmiQueryRequest Parse(string message)
    {
        var request = new WmiQueryRequest();
        var lines = message.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

        int i = 0;
        // Parse headers
        for (; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                i++;
                break; // Empty line indicates end of headers
            }

            if (line.StartsWith("HEADER:"))
            {
                var headerContent = line.Substring(7);
                var parts = headerContent.Split('=', 2);
                if (parts.Length == 2)
                {
                    request.Headers[parts[0].Trim()] = parts[1].Trim();
                }
            }
        }

        // The rest is the query
        if (i < lines.Length)
        {
            request.Query = string.Join("\n", lines.Skip(i));
        }

        return request;
    }
}
