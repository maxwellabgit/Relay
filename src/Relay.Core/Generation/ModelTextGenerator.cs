using Relay.Core.Model;

namespace Relay.Core.Generation;

/// <summary>Adapts an OpenAI-compatible local model client into the generation-only surface.</summary>
public sealed class ModelTextGenerator : ITextGenerator
{
    private readonly IModelClient _client;
    private readonly int _maxOutputTokens;

    public ModelTextGenerator(IModelClient client, int maxOutputTokens = 800)
    {
        _client = client;
        _maxOutputTokens = maxOutputTokens;
    }

    public string ProviderName => "local-model";

    public async Task<TextGenerationResult> GenerateAsync(
        TextGenerationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var evidence = request.EvidenceExcerpts.Count == 0
                ? ""
                : "Evidence:\n- " + string.Join("\n- ", request.EvidenceExcerpts.Take(8));
            var prompt = string.IsNullOrWhiteSpace(evidence)
                ? request.Prompt
                : evidence + "\n\nTask (" + request.TaskKind + "):\n" + request.Prompt;

            var messages = new ModelMessage[]
            {
                new("system", "You draft concise text for RELAY after code selected the generation task. Do not invent tools, permissions, or web facts."),
                new("user", prompt),
            };

            var response = await _client.CompleteAsync(
                new ModelRequest(_client.Model, messages, _maxOutputTokens, JsonObject: false),
                cancellationToken).ConfigureAwait(false);

            if (!response.Ok || string.IsNullOrWhiteSpace(response.Content))
                return TextGenerationResult.Fail(response.Error ?? "generator_unavailable");
            return TextGenerationResult.Success(response.Content.Trim());
        }
        catch (Exception ex)
        {
            return TextGenerationResult.Fail(ex.Message);
        }
    }
}
