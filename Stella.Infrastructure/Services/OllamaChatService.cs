using System.Text;
using System.Text.Json;
using Stella.Core.Interfaces;

namespace Stella.Infrastructure.Services;

public class OllamaChatService : ILLMService
{
    private readonly HttpClient _httpClient;
    private const string OllamaUrl = "http://localhost:11434/api/generate";

    public OllamaChatService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<string> GenerateCodeAsync(string prompt, string cartridgeId)
    {
        
        var requestBody = new
        {
            model = "deepseek-coder-v2:lite", 
            prompt = prompt,
            stream = false,
            system = "You are Stella, a professional Rust backend engineer. Output ONLY raw code without explanations."
        };

        var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync(OllamaUrl, content);

        if (!response.IsSuccessStatusCode)
            return $"Error: {response.ReasonPhrase}";

        var jsonResponse = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(jsonResponse);
        return doc.RootElement.GetProperty("response").GetString() ?? "No response";
    }
}