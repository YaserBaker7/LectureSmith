using LectureSmith.Services;
using System.Net;
using System.Net.Http;
using System.Text;
using Xunit;

namespace LectureSmith.Tests;

public class GeminiModelCatalogServiceTests
{
    private const string SamplePricingMarkdown = """
        ## Gemini 4 Carbon  {% id="gemini-4-carbon" %}
        *[`gemini-4-carbon`](/docs/models/gemini-4-carbon)*
        {% hr /%}
        Gemini 4 Carbon is not available on the Free tier.
        * Input price
        * Not available
        * $2.00
        * Output price (including thinking tokens)
        * Not available
        * $10.00

        ## Gemini 4 Hydrogen  {% id="gemini-4-hydrogen" %}
        *[`gemini-4-hydrogen`](/docs/models/gemini-4-hydrogen)*
        {% hr /%}
        * Input price
        * Free of charge
        * $1.50
        * Output price (including thinking tokens)
        * Free of charge
        * $7.50

        ## Gemini 3.8 Flash  {% id="gemini-3.8-flash" %}
        *[`gemini-3.8-flash`](/docs/models/gemini-3.8-flash)*
        {% hr /%}
        * Input price
        * Free of charge
        * $0.75
        * Output price (including thinking tokens)
        * Free of charge
        * $3.75

        ## Gemini 2.5 Pro  {% id="gemini-2.5-pro" %}
        *[`gemini-2.5-pro`](/docs/models/gemini-2.5-pro)*
        {% hr /%}
        {% callout type="caution" %}
        Gemini 2.5 Pro (`gemini-2.5-pro`) is [deprecated](/docs/deprecations). Routes to [`gemini-3.8-flash`](/docs/models/gemini-3.8-flash).
        {% /callout %}
        * Input price
        * Free of charge
        * $1.25
        """;

    private const string SampleModelsMarkdown = """
        ## All Gemini 4 models  {% id="all-gemini-4-models" %}
        * `gemini-4-carbon`
        ---
        * `gemini-4-hydrogen`

        ## Gemini 2.5 Pro  {% id="gemini-25-pro" %}
        `gemini-2.5-pro` is [deprecated](/docs/deprecations) and will route to `gemini-3.8-flash`.

        ## Previous models  {% id="previous-models" %}
        * [Gemini 2.0 Flash](/docs/models/gemini-2.0-flash) (Shut down)
        * `gemini-2.0-flash`

        ## Model version name patterns  {% id="model-versions" %}
        For example `gemini-3.6-flash` and `gemini-flash-latest` may be deprecated in future docs.
        """;

    [Fact]
    public void ParsePricingMarkdown_DoesNotLeakDeprecationRedirectIntoTargetModel()
    {
        var meta = GeminiModelCatalogService.MergeDocMetadata(SamplePricingMarkdown, SampleModelsMarkdown);

        Assert.True(meta.TryGetValue("gemini-4-hydrogen", out var h));
        Assert.True(h!.IsFree);
        Assert.False(h.IsDeprecated);

        Assert.True(meta.TryGetValue("gemini-4-carbon", out var c));
        Assert.False(c!.IsFree);

        Assert.True(meta.TryGetValue("gemini-3.8-flash", out var f38));
        Assert.True(f38!.IsFree);
        Assert.False(f38.IsDeprecated);

        Assert.True(meta.TryGetValue("gemini-2.5-pro", out var p25));
        Assert.True(p25!.IsDeprecated);

        Assert.True(meta.TryGetValue("gemini-2.0-flash", out var f20));
        Assert.True(f20!.IsShutDown);
    }

    [Theory]
    [InlineData("gemini-4-hydrogen", "Gemini 4 Hydrogen", true)]
    [InlineData("gemini-4-carbon", "Gemini 4 Carbon", false)]
    [InlineData("gemini-4-argon", "Gemini 4 Argon", false)]
    [InlineData("gemini-4-neon", "Gemini 4 Neon", false)]
    [InlineData("gemini-3.8-flash", "Gemini 3.8 Flash", true)]
    [InlineData("gemini-3.1-pro-preview", "Gemini 3.1 Pro Preview", false)]
    public void FallbackIsFreeTier_ClassifiesCorrectly(string id, string display, bool expectedFree)
    {
        Assert.Equal(expectedFree, GeminiModelCatalogService.FallbackIsFreeTier(id, display));
    }

    [Fact]
    public async Task ValidateAndFetchModelsAsync_FiltersNonTextModels_FollowsPagination_AndUsesApiKeyHeader()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(req =>
        {
            requests.Add(req);
            var url = req.RequestUri!.ToString();

            if (url.Contains("pricing.md.txt"))
                return TextResponse(SamplePricingMarkdown);
            if (url.Contains("models.md.txt"))
                return TextResponse(SampleModelsMarkdown);

            if (url.Contains("pageToken=page2"))
            {
                return JsonResponse("""
                {
                  "models": [
                    { "name": "models/gemini-4-carbon", "displayName": "Gemini 4 Carbon", "supportedGenerationMethods": ["generateContent"], "outputTokenLimit": 65536 },
                    { "name": "models/gemini-3.8-flash-tts", "displayName": "Gemini 3.8 Flash TTS", "supportedGenerationMethods": ["generateContent"] },
                    { "name": "models/gemini-embedding-001", "displayName": "Gemini Embedding", "supportedGenerationMethods": ["embedContent"] },
                    { "name": "models/gemma-4-27b-it", "displayName": "Gemma 4", "supportedGenerationMethods": ["generateContent"] }
                  ]
                }
                """);
            }

            return JsonResponse("""
            {
              "models": [
                { "name": "models/gemini-4-hydrogen", "displayName": "Gemini 4 Hydrogen", "supportedGenerationMethods": ["generateContent"], "outputTokenLimit": 65536 },
                { "name": "models/gemini-3.8-flash", "displayName": "Gemini 3.8 Flash", "supportedGenerationMethods": ["generateContent"], "outputTokenLimit": 65536 },
                { "name": "models/gemini-3.1-flash-image", "displayName": "Nano Banana 2", "supportedGenerationMethods": ["generateContent"] },
                { "name": "models/gemini-3.8-live", "displayName": "Gemini 3.8 Live", "supportedGenerationMethods": ["bidiGenerateContent"] },
                { "name": "models/gemini-2.0-flash", "displayName": "Gemini 2.0 Flash", "supportedGenerationMethods": ["generateContent"] }
              ],
              "nextPageToken": "page2"
            }
            """);
        });

        using var http = new HttpClient(handler);
        var catalog = new GeminiModelCatalogService(http);

        var (isValid, _, models) = await catalog.ValidateAndFetchModelsAsync("test-secret-key");

        Assert.True(isValid);

        // Verify API key was never sent in the URL query string
        var apiCalls = requests.Where(r => r.RequestUri!.Host == "generativelanguage.googleapis.com").ToList();
        Assert.Equal(2, apiCalls.Count);
        Assert.All(apiCalls, r =>
        {
            Assert.DoesNotContain("test-secret-key", r.RequestUri!.ToString());
            Assert.Equal("test-secret-key", r.Headers.GetValues("x-goog-api-key").Single());
        });

        // Should include ONLY non-shutdown Gemini text-generation models, sorted Free first
        Assert.Equal(["gemini-4-hydrogen", "gemini-3.8-flash", "gemini-4-carbon"], models.Select(m => m.Id).ToArray());
        Assert.True(models[0].IsFree);
        Assert.True(models[1].IsFree);
        Assert.False(models[2].IsFree);
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage TextResponse(string text)
        => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/plain") };

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
