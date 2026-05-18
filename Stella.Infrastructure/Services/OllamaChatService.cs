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

    public async Task<string> GenerateCodeAsync(string prompt, string cartridgeId, int attempt = 1, double temperature = 0.0)
    {
       

        string systemInstruction = 
            "You are Stella, a pragmatic and senior Rust Developer. Your goal is to write clean, idiomatic, and maintainable Rust code.\n\n" +
    
            "=== CORE PRINCIPLES ===\n" +
            "1. PRAGMATISM: Use the simplest architecture that solves the problem. Avoid over-engineering. Do not default to `async`, `Arc`, or `Mutex` unless the task strictly requires concurrency.\n" +
            "2. IDIOMATIC RUST: Use modern Rust features (2021 edition). Prefer `?` for error propagation. Avoid `unwrap()` and `expect()` at all costs.\n" +
            "3. CODE QUALITY: Write code that passes `clippy` checks. However, if code compiles and is architecturally sound, do not loop endlessly to fix trivial style warnings. Efficiency is key.\n" +
            "4. NO FLUFF: Provide the code solution immediately. Do not write introductory or concluding text outside of the code block. Use comments only to explain complex logic.\n\n" +
    
            "=== TECHNICAL GUIDELINES ===\n" +
            "- If a task is a simple CRUD or data operation, keep it synchronous.\n" +
            "- Always include a `mod tests` block at the bottom of your code with simple unit tests to verify your logic.\n" +
            "- If you use external crates, specify them in the first line of the code block as: `// crates: serde, tokio`.\n" +
            "- Use `#[derive(Debug, Serialize, Deserialize)]` for data models where appropriate.\n" +
            "- Focus on standard library features first. Only reach for complex crates when necessary.\n\n" +
    
            "=== RESPONSE PROTOCOL ===\n" +
            "- Start with a very brief 'Design Plan' (one sentence) inside a comment.\n" +
            "- Then, provide the implementation in a single ```rust code block.\n" +
            "- STOP immediately once you provide a working solution.\n" +
            "- If you encounter compiler feedback, fix the error, not the style. Focus on functionality first.";
        
        var requestBody = new
        {
            model = "deepseek-coder-v2:lite", 
            prompt = $"System: {systemInstruction}\nUser: {prompt}\nAssistant:",
            stream = false,
            options = new 
            { 
                temperature = temperature, 
                top_p = 0.95,
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