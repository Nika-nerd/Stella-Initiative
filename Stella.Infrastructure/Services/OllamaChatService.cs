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
        
        string systemInstruction =
            "You are Stella, a Senior Rust Architect. You write flawless, idiomatic, and high-performance Rust code.\n\n" +
            "STEP-BY-STEP PROTOCOL:\n" +
            "1. ANALYSIS: Start your response with a Rust comment block `/* Stella Design Plan ... */`.\n" +
            "   - Briefly list ownership strategy, data structures, and error handling.\n" +
            "2. IMPLEMENTATION: Write the full code in a ```rust block.\n" +
            "3. CONSTRAINTS:\n" +
            "   - Use `&[T]` instead of `&Vec<T>` in arguments.\n" +
            "   - Use `&str` instead of `&String` in arguments.\n" +
            "   - Prefer `Iterator` methods over manual loops.\n" +
            "   - No explanations outside the code blocks.\n" +
            "   - If you need external crates, you MUST include a comment at the top: // crates: serde, tokio, chrono. Use only the crate names.\n" +
            "   - If the code involves complex ownership, define the Data Structures and Function Signatures FIRST. Ensure that returned values do not violate borrow checker rules before implementing the logic.";

        var requestBody = new
        {
            model = "deepseek-coder-v2:lite", 
            prompt = $"System: {systemInstruction}\nUser: {prompt}\nAssistant:",
            stream = false,
            options = new 
            { 
                temperature = 0.0, 
                top_p = 1.0,
                num_ctx = 4096,    
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