using Relay.Core.Generation;
using Relay.Core.Model;

namespace Relay.Gateway;

/// <summary>
/// Local generation job client. Reuses OpenAI-compatible transport for bounded jobs only —
/// never used as a Jev judgment fallback.
/// </summary>
public sealed class LocalJobClient
{
    private readonly IModelClient? _model;

    public LocalJobClient(IModelClient? model = null) => _model = model;

    public bool Available => _model is not null;

    public async Task<string> GenerateAsync(LocalJobDefinition job, string prompt, CancellationToken cancellationToken = default)
    {
        if (_model is null)
            throw new InvalidOperationException("No local model client bound.");

        var response = await _model.CompleteAsync(new ModelRequest
        {
            System = "You perform bounded local extraction/drafting jobs. Do not claim external judgments. Cite source artifact ids when required.",
            User = prompt,
            MaxTokens = Math.Min(2048, Math.Max(64, job.OutputCharLimit / 4)),
        }, cancellationToken).ConfigureAwait(false);

        if (!response.Ok)
            throw new InvalidOperationException(response.Error ?? "local_job_failed");
        var content = response.Content ?? "";
        return content.Length <= job.OutputCharLimit ? content : content[..job.OutputCharLimit];
    }
}
