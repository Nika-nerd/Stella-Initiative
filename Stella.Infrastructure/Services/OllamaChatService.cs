using System.Text;
using System.IO;
using Microsoft.Extensions.Configuration;
using System.Text.Json;
using Stella.Core.Interfaces;

namespace Stella.Infrastructure.Services;

public class OllamaChatService : ILLMService
{
    private readonly HttpClient _httpClient;
    
    private const string ModelName = "gemini-2.5-flash";
    private const string ApiUrl = $"https://generativelanguage.googleapis.com/v1beta/models/{ModelName}:generateContent";
    
    private readonly string _apiKey;

    public OllamaChatService(HttpClient httpClient)
    {
        _httpClient = httpClient;

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .Build();
        
        _apiKey = configuration["Gemini:ApiKey"] ?? string.Empty;

        if (string.IsNullOrEmpty(_apiKey) || _apiKey == "MY_API_KEY")
        {
            System.Diagnostics.Debug.WriteLine("Gemini API Key is missing");
        }
    }

  public async Task<string> GenerateCodeAsync(string prompt, string cartridgeId, int attempt = 1, double temperature = 0.0)
{
    string systemInstruction = 
        "You are Stella, an expert Rust compiler assistant. Your absolute priority is code that COMPILES without errors.\n\n" +
        "=== STRICT COMPILATION RULES ===\n" +
        "1. STRICT TYPES: Match types perfectly. Pay attention to Ownership and Borrowing.\n" +
        "2. NO UNKNOWN CRATES: Use ONLY `serde`, `serde_json`, and `tokio` (full).\n" +
        "3. LIFETIMES: Use owned data (`String`, `Vec`) inside structs to avoid lifetime errors.\n\n" +
        "=== OUTPUT FORMAT ===\n" +
        "- Start directly with ```rust and end with ```.\n" +
        "- No explanations, no text outside the block.";

    var requestBody = new
    {
        contents = new[]
        {
            new { parts = new[] { new { text = $"System instruction:\n{systemInstruction}\n\nUser task:\n{prompt}" } } }
        },
        generationConfig = new
        {
            temperature = temperature,
            maxOutputTokens = 8192
        }
    };

    string requestUrl = $"{ApiUrl}?key={_apiKey}";
    
    int maxApiRetries = 3;
    int currentApiRetry = 0;

    while (true)
    {
        currentApiRetry++;
        var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
        
        try 
        {
            var response = await _httpClient.PostAsync(requestUrl, content);
            
            if (((int)response.StatusCode == 503 || (int)response.StatusCode == 429) && currentApiRetry <= maxApiRetries)
            {
                await Task.Delay(3000);
                continue; 
            }

            if (!response.IsSuccessStatusCode)
            {
                string errContent = await response.Content.ReadAsStringAsync();
                return $"Error: {response.StatusCode} - {response.ReasonPhrase}. Details: {errContent}";
            }

            var jsonResponse = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(jsonResponse);
            
            var candidates = doc.RootElement.GetProperty("candidates");
            var contentNode = candidates[0].GetProperty("content");
            var parts = contentNode.GetProperty("parts");
            return parts[0].GetProperty("text").GetString() ?? "No response";
        }
        catch (Exception ex) when (currentApiRetry <= maxApiRetries)
        {
            await Task.Delay(3000);
            continue;
        }
        catch (Exception ex)
        {
            return $"Connection error: {ex.Message}";
        }
    }
}
}