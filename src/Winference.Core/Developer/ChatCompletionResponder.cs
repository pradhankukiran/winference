using Winference.Core.Models;
using Winference.Core.Runtime;

namespace Winference.Core.Developer;

/// <summary>The model an app's request goes to: the one selected in Winference.</summary>
public sealed record ApiTarget(RuntimeBroker Broker, string ModelId, ModelProfile Profile);

/// <summary>What happened to one request, for the request log.</summary>
public sealed record ApiOutcome(int Status, string Summary, int? PromptTokens = null, int? CompletionTokens = null, double? TokensPerSecond = null);

/// <summary>Runs one OpenAI-style chat request against the loaded model and writes the reply.</summary>
public static class ChatCompletionResponder
{
    public static async Task<ApiOutcome> RespondAsync(ApiTarget target, ApiChatRequest request, IApiResponse response,
        double defaultTemperature, ReplyMeter? meter, TimeSpan busyWait, CancellationToken cancellationToken,
        TimeProvider? time = null)
    {
        time ??= TimeProvider.System;
        var id = "chatcmpl-" + Guid.NewGuid().ToString("N")[..24];
        var created = time.GetUtcNow().ToUnixTimeSeconds();
        var model = target.ModelId;
        var maxTokens = request.MaxTokens is { } asked ? Math.Min(asked, target.Profile.MaxOutputTokens) : (int?)null;
        var temperature = request.Temperature ?? defaultTemperature;
        var text = new System.Text.StringBuilder();
        var reasoning = new System.Text.StringBuilder();
        var pieces = 0;
        GenerationUsage? usage = null;
        GenerationEvent? terminal = null;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        TimeSpan? first = null;

        var stream = await OpenAsync(target.Broker, request.Messages, maxTokens, temperature, busyWait, cancellationToken).ConfigureAwait(false);
        meter?.Begin();
        try
        {
            if (request.Stream)
            {
                await response.BeginEventStreamAsync(cancellationToken).ConfigureAwait(false);
                await response.SendEventAsync(OpenAiApi.Chunk(id, created, model, "role", "assistant", null), cancellationToken).ConfigureAwait(false);
            }
            var item = stream.Current;
            while (true)
            {
                switch (item.Kind)
                {
                    case GenerationEventKind.TextDelta or GenerationEventKind.ReasoningDelta:
                        first ??= clock.Elapsed;
                        ++pieces;
                        meter?.Tick();
                        var field = item.Kind == GenerationEventKind.TextDelta ? "content" : "reasoning_content";
                        (item.Kind == GenerationEventKind.TextDelta ? text : reasoning).Append(item.Text);
                        if (request.Stream)
                            await response.SendEventAsync(OpenAiApi.Chunk(id, created, model, field, item.Text, null), cancellationToken).ConfigureAwait(false);
                        break;
                    case GenerationEventKind.Usage:
                        usage = item.Usage;
                        break;
                    default:
                        terminal = item;
                        break;
                }
                if (terminal is not null || !await stream.MoveNextAsync().ConfigureAwait(false)) break;
                item = stream.Current;
            }
        }
        finally
        {
            meter?.End();
            await stream.DisposeAsync().ConfigureAwait(false);
        }

        var prompt = usage?.PromptTokens ?? 0;
        var completion = usage?.CompletionTokens ?? pieces;
        double? rate = first is { } start ? ReplyRate(usage, pieces, clock.Elapsed - start) : null;
        if (terminal?.Kind == GenerationEventKind.Completed)
        {
            var finish = terminal.FinishReason is "length" ? "length" : "stop";
            if (request.Stream)
            {
                await response.SendEventAsync(OpenAiApi.Chunk(id, created, model, null, null, finish), cancellationToken).ConfigureAwait(false);
                if (request.IncludeUsage)
                    await response.SendEventAsync(OpenAiApi.UsageChunk(id, created, model, prompt, completion), cancellationToken).ConfigureAwait(false);
                await response.SendEventAsync("[DONE]", cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await response.SendJsonAsync(200, OpenAiApi.Completion(id, created, model, text.ToString(), reasoning.ToString(), finish,
                    prompt, completion), cancellationToken).ConfigureAwait(false);
            }
            return new(200, finish == "length" ? "Answered (cut short)" : "Answered", usage?.PromptTokens, completion, rate);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var stopped = terminal is null || terminal.Kind == GenerationEventKind.Cancelled;
        var message = stopped ? "The reply was stopped in Winference." : terminal!.Error ?? "The model couldn't finish the reply.";
        var status = stopped ? 503 : 500;
        var code = stopped ? "reply_stopped" : "reply_failed";
        if (request.Stream)
        {
            await response.SendEventAsync(OpenAiApi.Error(status, code, message), cancellationToken).ConfigureAwait(false);
            await response.SendEventAsync("[DONE]", cancellationToken).ConfigureAwait(false);
        }
        else await response.SendJsonAsync(status, OpenAiApi.Error(status, code, message), cancellationToken).ConfigureAwait(false);
        return new(status, stopped ? "Stopped" : "Failed: " + message, usage?.PromptTokens, completion, rate);
    }

    /// <summary>
    /// Starts the reply and reads its first event, so problems found before any text
    /// (a busy model, a message the engine refuses) can still be sent as a plain error.
    /// </summary>
    private static async Task<IAsyncEnumerator<GenerationEvent>> OpenAsync(RuntimeBroker broker, IReadOnlyList<ChatMessage> messages,
        int? maxTokens, double temperature, TimeSpan busyWait, CancellationToken cancellationToken)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var stream = broker.GenerateAsync(messages, maxTokens, cancellationToken, temperature).GetAsyncEnumerator(cancellationToken);
            try
            {
                if (await stream.MoveNextAsync().ConfigureAwait(false)) return stream;
                await stream.DisposeAsync().ConfigureAwait(false);
                throw new ApiException(500, "reply_failed", "The model ended the reply before it started.");
            }
            catch (RuntimeException error) when (error.Code == "Busy")
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                if (waited.Elapsed >= busyWait)
                    throw new ApiException(503, "model_busy", "The model is busy with another reply. Try again shortly.");
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            }
            catch (RuntimeException error)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                throw ToApiError(error);
            }
            catch
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    public static ApiException ToApiError(RuntimeException error) => error.Code switch
    {
        "NotReady" => new(503, "model_not_ready", error.Message),
        "OutputLimit" or "MessageLimit" or "InvalidMessage" or "PayloadLimit" or "ImagesUnsupported" or "InvalidImage"
            or "context_exceeded" or "invalid_message" => new(400, error.Code, error.Message),
        _ => new(500, error.Code, error.Message)
    };

    private static double? ReplyRate(GenerationUsage? usage, int pieces, TimeSpan writing)
    {
        if (usage?.DecodeTokensPerSecond is { } reported) return reported;
        var tokens = usage?.CompletionTokens ?? pieces;
        return writing.TotalSeconds > 0.05 && tokens > 1 ? (tokens - 1) / writing.TotalSeconds : null;
    }
}
