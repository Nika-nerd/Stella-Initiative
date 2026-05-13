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
        
        string systemInstruction = "You are Stella, a professional Rust engineer. " +
                                   "Rules: Output ONLY valid Rust code. No talk. No markdown explanations. " +
                                   "Wrap code in ```rust blocks.";

        var requestBody = new
        {
            model = "deepseek-coder-v2:lite", 
            
            prompt = $"System: {systemInstruction}\nUser: {prompt}\nAssistant:",
            stream = false,
            options = new 
            { 
                temperature = 0.2, 
                top_p = 0.9,
                stop = new[] { "User:", "System:" } 
            }
        };

        var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
    
        try 
        {
            var response = await _httpClient.PostAsync(OllamaUrl, content);

            if (!response.IsSuccessStatusCode)
                return $"Error: {response.ReasonPhrase}";

            var jsonResponse = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(jsonResponse);
        
            
            string aiResponse = doc.RootElement.GetProperty("response").GetString() ?? "No response";
        
            return aiResponse;
        }
        catch (Exception ex)
        {
            return $"Connection error: {ex.Message}";
        }
    }
}