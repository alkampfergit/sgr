using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;

namespace Alkampfer.Sgr.Telemetry;

/// <summary>
/// Centralized tracing helpers for Schema-Guided Reasoning operations.
/// </summary>
public static class SgrTelemetry
{
    private const string ScenarioNameTag = "sgr.scenario.name";
    private const string ReasonerModeTag = "sgr.reasoner.mode";
    private const string ToolNameTag = "sgr.tool.name";

    public const string ActivitySourceName = "Alkampfer.Sgr";
    public const string MeterName = "Alkampfer.Sgr";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    public static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> ConversationStartedCounter = Meter.CreateCounter<long>("sgr.conversation.started");
    private static readonly Counter<long> ConversationCompletedCounter = Meter.CreateCounter<long>("sgr.conversation.completed");
    private static readonly Counter<long> ConversationFailedCounter = Meter.CreateCounter<long>("sgr.conversation.failed");
    private static readonly UpDownCounter<long> ActiveConversationCounter = Meter.CreateUpDownCounter<long>("sgr.conversation.active");
    private static readonly Histogram<double> ConversationDurationHistogram = Meter.CreateHistogram<double>("sgr.conversation.duration", unit: "s");
    private static readonly Counter<long> LlmCallCounter = Meter.CreateCounter<long>("sgr.llm.calls");
    private static readonly Counter<long> LlmFailureCounter = Meter.CreateCounter<long>("sgr.llm.failures");
    private static readonly Histogram<double> LlmCallDurationHistogram = Meter.CreateHistogram<double>("sgr.llm.call.duration", unit: "s");
    private static readonly Histogram<long> LlmInputTokensHistogram = Meter.CreateHistogram<long>("sgr.llm.input_tokens");
    private static readonly Histogram<long> LlmOutputTokensHistogram = Meter.CreateHistogram<long>("sgr.llm.output_tokens");
    private static readonly Histogram<long> LlmTotalTokensHistogram = Meter.CreateHistogram<long>("sgr.llm.total_tokens");
    private static readonly Counter<long> ToolCallCounter = Meter.CreateCounter<long>("sgr.tool.calls");
    private static readonly Counter<long> ToolFailureCounter = Meter.CreateCounter<long>("sgr.tool.failures");
    private static readonly Histogram<double> ToolDurationHistogram = Meter.CreateHistogram<double>("sgr.tool.duration", unit: "s");

    public static Activity? StartScenarioActivity(string scenarioName, string reasonerMode, string? userRequest = null)
    {
        var tags = CreateScenarioTags(scenarioName, reasonerMode);
        ConversationStartedCounter.Add(1, tags);
        ActiveConversationCounter.Add(1, tags);

        var activity = ActivitySource.StartActivity("sgr.scenario", ActivityKind.Internal);
        if (activity == null)
        {
            return null;
        }

        activity.DisplayName = scenarioName;
        activity.SetTag(ScenarioNameTag, scenarioName);
        activity.SetTag(ReasonerModeTag, reasonerMode);
        SetIfNotEmpty(activity, "sgr.user.request", userRequest);
        return activity;
    }

    public static Activity? StartReasonerExecution(string reasonerKind, string userRequest)
    {
        var activity = ActivitySource.StartActivity("sgr.reasoner.execute", ActivityKind.Internal);
        if (activity == null)
        {
            return null;
        }

        activity.DisplayName = $"{reasonerKind} execution";
        activity.SetTag("sgr.reasoner.kind", reasonerKind);
        SetIfNotEmpty(activity, "sgr.user.request", userRequest);
        return activity;
    }

    public static Activity? StartPlanningStep(string reasonerKind, int stepNumber)
    {
        var activity = ActivitySource.StartActivity("sgr.reasoner.step", ActivityKind.Internal);
        if (activity == null)
        {
            return null;
        }

        activity.DisplayName = $"{reasonerKind} step {stepNumber}";
        activity.SetTag("sgr.reasoner.kind", reasonerKind);
        activity.SetTag("sgr.step.number", stepNumber);
        return activity;
    }

    public static Activity? StartModelCall(
        string provider,
        string model,
        string systemPrompt,
        string userPrompt,
        string schema)
    {
        var activity = ActivitySource.StartActivity("sgr.model.call", ActivityKind.Client);
        if (activity == null)
        {
            return null;
        }

        activity.DisplayName = $"{provider}:{model}";
        activity.SetTag("gen_ai.system", provider);
        activity.SetTag("gen_ai.request.model", model);
        SetIfNotEmpty(activity, "sgr.prompt.system", systemPrompt);
        SetIfNotEmpty(activity, "sgr.prompt.user", userPrompt);
        SetIfNotEmpty(activity, "sgr.prompt.schema", schema);
        return activity;
    }

    public static Activity? StartToolCall(string toolName, object? parameters = null)
    {
        ToolCallCounter.Add(1, new TagList
        {
            { ToolNameTag, toolName }
        });

        var activity = ActivitySource.StartActivity("sgr.tool.execute", ActivityKind.Internal);
        if (activity == null)
        {
            return null;
        }

        activity.DisplayName = toolName;
        activity.SetTag(ToolNameTag, toolName);
        SetIfNotEmpty(activity, "sgr.tool.parameters", Serialize(parameters));
        return activity;
    }

    public static void RecordModelResponse(Activity? activity, string? assistantResponse)
    {
        SetIfNotEmpty(activity, "sgr.response.content", assistantResponse);
    }

    public static void RecordUsage(Activity? activity, int? inputTokens, int? outputTokens, int? totalTokens)
    {
        var tags = CreateTagsFromActivity(activity);

        if (activity == null)
        {
            tags = default;
        }

        if (inputTokens.HasValue)
        {
            activity?.SetTag("sgr.usage.input_tokens", inputTokens.Value);
            LlmInputTokensHistogram.Record(inputTokens.Value, tags);
        }

        if (outputTokens.HasValue)
        {
            activity?.SetTag("sgr.usage.output_tokens", outputTokens.Value);
            LlmOutputTokensHistogram.Record(outputTokens.Value, tags);
        }

        if (totalTokens.HasValue)
        {
            activity?.SetTag("sgr.usage.total_tokens", totalTokens.Value);
            LlmTotalTokensHistogram.Record(totalTokens.Value, tags);
        }
    }

    public static void RecordToolResult(Activity? activity, string? summary)
    {
        SetIfNotEmpty(activity, "sgr.tool.summary", summary);
    }

    public static void MarkError(Activity? activity, Exception exception)
    {
        if (activity == null)
        {
            return;
        }

        activity.SetStatus(ActivityStatusCode.Error, exception.Message);
        activity.SetTag("exception.type", exception.GetType().FullName);
        activity.SetTag("exception.message", exception.Message);
    }

    public static void RecordConversationCompleted(string scenarioName, string reasonerMode, TimeSpan duration)
    {
        var tags = CreateScenarioTags(scenarioName, reasonerMode);

        ConversationCompletedCounter.Add(1, tags);
        ActiveConversationCounter.Add(-1, tags);
        ConversationDurationHistogram.Record(duration.TotalSeconds, tags);
    }

    public static void RecordConversationFailed(string scenarioName, string reasonerMode, TimeSpan duration)
    {
        var tags = CreateScenarioTags(scenarioName, reasonerMode);

        ConversationFailedCounter.Add(1, tags);
        ActiveConversationCounter.Add(-1, tags);
        ConversationDurationHistogram.Record(duration.TotalSeconds, tags);
    }

    public static void RecordLlmCallStarted(string provider, string model)
    {
        LlmCallCounter.Add(1, new TagList
        {
            { "gen_ai.system", provider },
            { "gen_ai.request.model", model }
        });
    }

    public static void RecordLlmCallCompleted(Activity? activity, TimeSpan duration)
    {
        LlmCallDurationHistogram.Record(duration.TotalSeconds, CreateTagsFromActivity(activity));
    }

    public static void RecordLlmCallFailed(Activity? activity, TimeSpan duration)
    {
        var tags = CreateTagsFromActivity(activity);
        LlmFailureCounter.Add(1, tags);
        LlmCallDurationHistogram.Record(duration.TotalSeconds, tags);
    }

    public static void RecordToolCompleted(Activity? activity, string toolName, TimeSpan duration)
    {
        var tags = CreateTagsFromActivity(activity);
        if (!tags.Any(static pair => pair.Key == ToolNameTag))
        {
            tags.Add(ToolNameTag, toolName);
        }

        ToolDurationHistogram.Record(duration.TotalSeconds, tags);
    }

    public static void RecordToolFailed(Activity? activity, string toolName, TimeSpan duration)
    {
        var tags = CreateTagsFromActivity(activity);
        if (!tags.Any(static pair => pair.Key == ToolNameTag))
        {
            tags.Add(ToolNameTag, toolName);
        }

        ToolFailureCounter.Add(1, tags);
        ToolDurationHistogram.Record(duration.TotalSeconds, tags);
    }

    public static string? GetCurrentTraceId() => Activity.Current?.TraceId.ToString();

    private static void SetIfNotEmpty(Activity? activity, string key, string? value)
    {
        if (activity == null || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        activity.SetTag(key, value);
    }

    private static string? Serialize(object? value)
    {
        if (value == null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Serialize(value);
        }
        catch
        {
            return value.ToString();
        }
    }

    private static TagList CreateTagsFromActivity(Activity? activity)
    {
        var tags = new TagList();

        if (activity == null)
        {
            return tags;
        }

        foreach (var tag in activity.Tags)
        {
            if (tag.Value != null)
            {
                tags.Add(tag.Key, tag.Value);
            }
        }

        return tags;
    }

    private static TagList CreateScenarioTags(string scenarioName, string reasonerMode) =>
        new()
        {
            { ScenarioNameTag, scenarioName },
            { ReasonerModeTag, reasonerMode }
        };
}
