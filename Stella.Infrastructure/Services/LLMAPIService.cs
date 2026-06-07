using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Stella.Core.Interfaces;

namespace Stella.Infrastructure.Services;

public class LLMAPIService : ILLMService
{
    private readonly HttpClient _httpClient;
    private const string ModelName = "gemini-2.5-flash";
    private const string ApiUrl = $"https://generativelanguage.googleapis.com/v1beta/models/{ModelName}:generateContent";
    private readonly string _apiKey;

    public LLMAPIService(HttpClient httpClient)
    {
        _httpClient = httpClient;

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .Build();
        
        _apiKey = configuration["Gemini:ApiKey"] ?? string.Empty;
    }

    public async Task<string> GenerateCodeAsync(string prompt, int attempt = 1, double temperature = 0.0)
    {
        string baseInstruction = 
            "You are an automated Rust code generation agent. Your sole objective is to write production-grade, highly optimized, and sound Rust code based on user specifications.\n" +
            "=== COMPILATION & QUALITY REQUIREMENTS ===\n" +
            "1. ZERO ERRORS: Code must strictly compile via rustc, pass all clippy lints (pedantic), and satisfy the borrow checker.\n" +
            "2. DEPENDENCIES: Use ONLY the Rust Standard Library (std). No external crates are allowed unless specified.\n" +
            "3. TESTING: Always include a `mod tests` block with comprehensive `#[test]` functions at the bottom of the code.\n" +
            "=== NO COMMENTS RULE ===\n" +
            "4. ABSOLUTELY NO CODE COMMENTS: Do not write ANY comments inside the Rust code block. The code must be self-explanatory.\n";

        string formattingInstruction;
        int maxTokens;

        if (attempt == 1)
        {
            formattingInstruction = 
                "=== RESPONSE STRUCTURE FORMAT ===\n" +
                "Your response MUST follow this exact layout:\n\n" +
                "[ANALYSIS & PLANNING]\n" +
                "Provide a brief, high-level structural plan of your solution here in plain English text. No code blocks.\n\n" +
                "[CODE BLOCK]\n" +
                "Provide the complete, executable Rust code inside exactly one markdown block starting with ```rust and ending with ```.";
            maxTokens = 6144; 
        }
        else
        {
           
            formattingInstruction = 
                "=== CRITICAL DIRECTIVE FOR BUG-FIXING ATTEMPT ===\n" +
                "This is a correction attempt. DO NOT output [ANALYSIS & PLANNING] section.\n" +
                "Your output must contain ONLY the [CODE BLOCK] section. Go straight to the ```rust block.";
            maxTokens = 4096;
        }

        string fullSystemInstruction = baseInstruction + formattingInstruction;

        var requestBody = new
        {
            systemInstruction = new
            {
                parts = new[] { new { text = fullSystemInstruction } }
            },
            contents = new[]
            {
                new { parts = new[] { new { text = prompt } } }
            },
            generationConfig = new
            {
                temperature = temperature,
                maxOutputTokens = maxTokens
            }
        };

        string requestUrl = $"{ApiUrl}?key={_apiKey}";
        int maxApiRetries = 5; 

        for (int i = 0; i < maxApiRetries; i++)
        {
            try
            {
                var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync(requestUrl, content);

                if (((int)response.StatusCode == 429 || (int)response.StatusCode == 503) && i < maxApiRetries - 1)
                {
                    int delay = (int)Math.Pow(2, i + 1) * 1000; 
                    await Task.Delay(delay);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    string errContent = await response.Content.ReadAsStringAsync();
                    return $"Error: {response.StatusCode}. Details: {errContent}";
                }

                var jsonResponse = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(jsonResponse);
                
                return doc.RootElement
                    .GetProperty("candidates")[0]
                    .GetProperty("content")
                    .GetProperty("parts")[0]
                    .GetProperty("text").GetString() ?? "No response";
            }
            catch when (i < maxApiRetries - 1)
            {
                int delay = (int)Math.Pow(2, i + 1) * 1000;
                await Task.Delay(delay);
            }
        }
        return "Connection error: Failed after retries.";
    }
}