using System.Diagnostics;
using System.Text.Json;

namespace Alkampfer.Sgr.Telemetry;

/// <summary>
/// Centralized tracing helpers for Schema-Guided Reasoning operations.
/// </summary>
public static class SgrTelemetry
{
    public const string ActivitySourceName = "Alkampfer.Sgr";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    public static Activity? StartScenarioActivity(string scenarioName, string reasonerMode, string? userRequest = null)
    {
        var activity = ActivitySource.StartActivity("sgr.scenario", ActivityKind.Internal);
        if (activity == null)
        {
            return null;
        }

        activity.DisplayName = scenarioName;
        activity.SetTag("sgr.scenario.name", scenarioName);
        activity.SetTag("sgr.reasoner.mode", reasonerMode);
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
        var activity = ActivitySource.StartActivity("sgr.tool.execute", ActivityKind.Internal);
        if (activity == null)
        {
            return null;
        }

        activity.DisplayName = toolName;
        activity.SetTag("sgr.tool.name", toolName);
        SetIfNotEmpty(activity, "sgr.tool.parameters", Serialize(parameters));
        return activity;
    }

    public static void RecordModelResponse(Activity? activity, string? assistantResponse)
    {
        SetIfNotEmpty(activity, "sgr.response.content", assistantResponse);
    }

    public static void RecordUsage(Activity? activity, int? inputTokens, int? outputTokens, int? totalTokens)
    {
        if (activity == null)
        {
            return;
        }

        if (inputTokens.HasValue)
        {
            activity.SetTag("sgr.usage.input_tokens", inputTokens.Value);
        }

        if (outputTokens.HasValue)
        {
            activity.SetTag("sgr.usage.output_tokens", outputTokens.Value);
        }

        if (totalTokens.HasValue)
        {
            activity.SetTag("sgr.usage.total_tokens", totalTokens.Value);
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
}
