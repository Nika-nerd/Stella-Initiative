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

    public async Task<string> GenerateCodeAsync(string prompt, string cartridgeId, int attempt = 1)
    {
        double currentTemperature = attempt == 1 ? 0.0 : 0.2;

        string systemInstruction =
            "You are Stella, an elite AI Rust Architect specializing in bulletproof, production-ready systems.\n\n" +
            "=== SYSTEM PROTOCOL ===\n" +
            "1. DESIGN PLAN: Start your response with a `/* Stella Design Plan ... */` comment block.\n" +
            "   - Briefly list ownership strategy, data structures, and error handling.\n" +
            "2. IMPLEMENTATION: Write the full code inside a single ```rust block.\n" +
            "3. TESTING: You MUST include comprehensive unit tests inside a `#[cfg(test)] mod tests` block at the bottom.\n\n" +
            "=== STRICT RUST RULES ===\n" +
            "- NO manual loops if `Iterator` methods (`map`, `filter`, `fold`) can be used.\n" +
            "- NO `&Vec<T>` or `&String` in function signatures. Use `&[T]` and `&str` exclusively.\n" +
            "- Use strict idiomatic error handling: return `Result<T, E>` or `Option<T>`. Avoid `unwrap()` or `expect()` in production code.\n" +
            "- All external crates must be explicitly declared in the top comment: `// crates: serde, tokio`.\n\n" +
            "=== TEST-DRIVEN CONSTRAINTS ===\n" +
            "- Keep unit tests minimal, isolated, and highly focused.\n" +
            "- Do not use complex mocking or external test frameworks. Use standard `assert_eq!` and `assert!`.\n" +
            "- Test edge cases (empty inputs, bounds) but avoid writing massive boilerplate code.\n\n" +
            "=== ANTI-PATTERNS (CRITICAL) ===\n" +
            "- DO NOT write introductory or concluding text outside the code blocks.\n" +
            "- DO NOT rewrite structural definitions (structs/enums) on repair attempts unless they are the direct cause of the compiler error." +
            "- NEVER use `.unwrap()` or `.expect()`. Use `let else`, `unwrap_or`, or `?`." +
            "- NEVER use manual index looping (`for i in 0..vec.len()`). Use `.iter()` or `.enumerate()`." +
            "- NEVER use `unsafe` blocks unless explicitly requested." +
            "- If a type implements `Copy`, pass it by value, not by reference.";
        
        var requestBody = new
        {
            model = "deepseek-coder-v2:lite", 
            prompt = $"System: {systemInstruction}\nUser: {prompt}\nAssistant:",
            stream = false,
            options = new 
            { 
                temperature = currentTemperature, 
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