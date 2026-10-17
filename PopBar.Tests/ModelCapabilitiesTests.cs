using PopBar.Core.Llm;
using PopBar.Sdk;

namespace PopBar.Tests;

public class ModelCapabilitiesTests
{
    [Theory]
    [InlineData("""{ "id": "a", "architecture": { "input_modalities": ["text", "image"], "output_modalities": ["text"] } }""", LlmFeatures.Vision)]
    [InlineData("""{ "id": "a", "architecture": { "input_modalities": ["text"], "output_modalities": ["text", "audio"] } }""", LlmFeatures.Speech)]
    [InlineData("""{ "id": "a", "architecture": { "input_modalities": ["text"], "output_modalities": ["text"] } }""", LlmFeatures.None)]
    [InlineData("""{ "id": "a", "capabilities": { "vision": true } }""", LlmFeatures.Vision)]
    [InlineData("""{ "id": "a", "object": "model", "owned_by": "x" }""", null)] // says nothing: unknown, not "no"
    public void Reads_what_a_model_list_entry_says(string json, LlmFeatures? expected)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(expected, ModelCapabilities.FromModelEntry(doc.RootElement));
    }
}

public class ModelCatalogTests
{
    private static string Model(string id, string inputs, string outputs = "\"text\"", string? slug = null) =>
        $$"""{ "id": "{{id}}", {{(slug == null ? "" : $"\"canonical_slug\": \"{slug}\",")}} "architecture": { "input_modalities": [{{inputs}}], "output_modalities": [{{outputs}}] } }""";

    private static ModelCatalog Catalog()
    {
        var c = new ModelCatalog(new System.Net.Http.HttpClient(), System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"popbar-catalog-{Guid.NewGuid():N}.json"));
        c.Load("{ \"data\": [" + string.Join(",",
            Model("openai/gpt-4o", "\"text\",\"image\"", slug: "openai/gpt-4o-2024-08-06"),
            Model("openai/gpt-4o-mini", "\"text\",\"image\""),
            Model("openai/gpt-4o-audio-preview", "\"text\",\"audio\"", "\"text\",\"audio\""),
            Model("anthropic/claude-opus-5.5", "\"text\",\"image\""),
            Model("deepseek/deepseek-chat", "\"text\""),
            Model("qwen/qwen3-vl-235b-a22b-instruct", "\"text\",\"image\""),
            Model("x-ai/grok-4", "\"text\",\"image\""),
            Model("meta-llama/llama-3.3-70b-instruct:free", "\"text\""),
            Model("~deepseek/deepseek-flash-latest", "\"text\",\"image\"")) + "] }");
        return c;
    }

    [Theory]
    [InlineData("openai/gpt-4o", LlmFeatures.Vision)]             // OpenRouter's own id
    [InlineData("gpt-4o", LlmFeatures.Vision)]                    // a vendor's id
    [InlineData("gpt-4o-2024-08-06", LlmFeatures.Vision)]         // the dated release
    [InlineData("gpt-4o-mini-2024-07-18", LlmFeatures.Vision)]    // dated, and not taken for gpt-4o
    [InlineData("gpt-4o-audio-preview", LlmFeatures.Speech)]      // audio in and out: speaks, and audio input is no image
    [InlineData("claude-opus-5-5", LlmFeatures.Vision)]           // dots vs dashes
    [InlineData("deepseek-chat", LlmFeatures.None)]
    [InlineData("qwen3-vl:235b", LlmFeatures.Vision)]             // Ollama's name with a size tag
    [InlineData("grok-4", LlmFeatures.Vision)]
    [InlineData("llama-3.3-70b-instruct", LlmFeatures.None)]      // OpenRouter's :free variant
    [InlineData("deepseek-flash", LlmFeatures.Vision)]            // the "-latest" alias it stands for
    [InlineData("gpt-4o-search-preview", null)]                   // another model, not gpt-4o: unknown
    [InlineData("deepseek-reasoner", null)]                       // not listed: unknown
    [InlineData("my-finetune", null)]
    [InlineData("", null)]
    public void Matches_any_services_model_id_to_the_catalog(string model, LlmFeatures? expected)
        => Assert.Equal(expected, Catalog().Lookup(model));

    [Fact]
    public void Nothing_is_known_before_the_catalog_loads() =>
        Assert.Null(new ModelCatalog(new System.Net.Http.HttpClient(), System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"popbar-catalog-{Guid.NewGuid():N}.json")).Lookup("gpt-4o"));
}

public class ReasoningDialectTests
{
    private static IDictionary<string, object> Body(ILlmProvider provider, string model, bool thinking)
    {
        // the dialects are internal; drive them the way the provider does
        var dialect = provider.Id switch { "openrouter" => (IChatCompletionsDialect)new OpenRouterDialect(), _ => new GrokDialect() };
        var body = new Dictionary<string, object> { ["model"] = model };
        dialect.AddReasoning(body, new LlmRequest { User = "hi", Thinking = thinking, Effort = LlmEffort.High });
        return body;
    }

    private static ILlmProvider P(string id) => BuiltinProviders.Create().Single(p => p.Id == id);

    [Fact]
    public void OpenRouter_switches_reasoning_with_its_one_object()
    {
        Assert.Equal("{\"effort\":\"high\"}", System.Text.Json.JsonSerializer.Serialize(Body(P("openrouter"), "anthropic/claude-sonnet-4", true)["reasoning"]));
        Assert.Equal("{\"enabled\":false}", System.Text.Json.JsonSerializer.Serialize(Body(P("openrouter"), "anthropic/claude-sonnet-4", false)["reasoning"]));
    }

    [Fact]
    public void Grok_sends_effort_only_to_the_models_that_take_it()
    {
        Assert.Equal("high", Body(P("xai"), "grok-3-mini", true)[ChatCompletionsFields.ReasoningEffort]);
        Assert.False(Body(P("xai"), "grok-4", true).ContainsKey(ChatCompletionsFields.ReasoningEffort));
        Assert.Empty(P("xai").EffortLevels("grok-4"));
        Assert.Equal([LlmEffort.Low, LlmEffort.High], P("xai").EffortLevels("grok-3-mini"));
    }
}
