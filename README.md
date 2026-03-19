# Alkampfer.Sgr

`Alkampfer.Sgr` is a schema-guided reasoning library with a console playground used to run end-to-end scenarios against Azure OpenAI and optional SQL Server workflows.

This repository also includes a local OpenTelemetry setup that can export traces, metrics, and logs to an OTLP endpoint and visualize them in the Aspire dashboard.

For the Aspire-specific setup and dashboard workflow, see [ASPIRE.md](/mnt/a/Develop/github/sgr/ASPIRE.md).

## What Is Integrated

OpenTelemetry is integrated in two layers:

- `src/Alkampfer.Sgr` contains the shared telemetry surface used by the reasoning engine.
- `src/Alkampfer.Sgr.Playground` configures the actual OpenTelemetry SDK providers and OTLP exporters.

That split is intentional:

- the reusable library creates activities and metrics without depending on a specific host
- the playground decides where telemetry is exported
- the Aspire dashboard is used as local development tooling by pointing OTLP export to its ingest endpoint

## Project Structure

- `src/Alkampfer.Sgr/Telemetry/SgrTelemetry.cs`
  Defines the shared `ActivitySource`, `Meter`, helper methods for spans, and custom counters/histograms.
- `src/Alkampfer.Sgr.Playground/PlaygroundTelemetry.cs`
  Creates the `TracerProvider`, `MeterProvider`, and OpenTelemetry log exporter.
- `src/Alkampfer.Sgr.Playground/Program.cs`
  Starts the scenario root activity and prints the active trace id to the console.
- `src/Alkampfer.Sgr/Runtime/ResponseApiSchemaGuidedReasoner.cs`
  Creates spans around reasoning execution, planning steps, and model calls.
- `src/Alkampfer.Sgr/Runtime/ResponseApiForcedToolSchemaGuidedReasoner.cs`
  Creates telemetry for the forced-tool two-phase reasoning flow.
- `src/Alkampfer.Sgr/Runtime/BusinessFunctionFactory.cs`
  Wraps business tool execution in tool spans and records tool outcomes.

## Telemetry Architecture

Each playground run produces one trace tree rooted at the selected scenario.

Typical span hierarchy:

```text
sgr.scenario
  sgr.reasoner.execute
    sgr.reasoner.step
      sgr.model.call
      sgr.tool.execute
    sgr.reasoner.step
      sgr.model.call
      sgr.tool.execute
```

The important design choice is that everything relies on `Activity.Current`. Once the root scenario activity starts, nested spans created inside async calls automatically join the same trace.

## Where OpenTelemetry Is Configured

The playground owns SDK setup in `src/Alkampfer.Sgr.Playground/PlaygroundTelemetry.cs`.

### Packages

The playground references:

- `OpenTelemetry`
- `OpenTelemetry.Exporter.OpenTelemetryProtocol`
- `OpenTelemetry.Instrumentation.Http`
- `OpenTelemetry.Instrumentation.SqlClient`

See [src/Alkampfer.Sgr.Playground/Alkampfer.Sgr.Playground.csproj](/mnt/a/Develop/github/sgr/src/Alkampfer.Sgr.Playground/Alkampfer.Sgr.Playground.csproj).

### Startup Configuration

The playground creates:

- a logger factory with console logging and OpenTelemetry log export
- a tracer provider for custom spans plus HTTP and SQL client instrumentation
- a meter provider for custom SGR metrics

This is the key setup:

```csharp
using System.Diagnostics;
using Alkampfer.Sgr.Telemetry;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

Activity.DefaultIdFormat = ActivityIdFormat.W3C;
Activity.ForceDefaultIdFormat = true;

var resourceBuilder = ResourceBuilder.CreateDefault()
    .AddService("Alkampfer.Sgr.Playground", serviceVersion: serviceVersion);

var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.Configure(options =>
    {
        options.ActivityTrackingOptions =
            ActivityTrackingOptions.TraceId |
            ActivityTrackingOptions.SpanId |
            ActivityTrackingOptions.ParentId;
    });

    builder.AddSimpleConsole();

    builder.AddOpenTelemetry(options =>
    {
        options.SetResourceBuilder(resourceBuilder);
        options.IncludeFormattedMessage = true;
        options.IncludeScopes = true;
        options.ParseStateValues = true;
        options.AddOtlpExporter(exporter =>
        {
            exporter.Endpoint = new Uri(endpoint);
            exporter.Protocol = OtlpExportProtocol.Grpc;
        });
    });
});

var tracerProvider = Sdk.CreateTracerProviderBuilder()
    .SetResourceBuilder(resourceBuilder)
    .AddSource(SgrTelemetry.ActivitySourceName)
    .AddHttpClientInstrumentation()
    .AddSqlClientInstrumentation()
    .AddOtlpExporter(exporter =>
    {
        exporter.Endpoint = new Uri(endpoint);
        exporter.Protocol = OtlpExportProtocol.Grpc;
    })
    .Build();

var meterProvider = Sdk.CreateMeterProviderBuilder()
    .SetResourceBuilder(resourceBuilder)
    .AddMeter(SgrTelemetry.MeterName)
    .AddOtlpExporter(exporter =>
    {
        exporter.Endpoint = new Uri(endpoint);
        exporter.Protocol = OtlpExportProtocol.Grpc;
    })
    .Build();
```

Source: [src/Alkampfer.Sgr.Playground/PlaygroundTelemetry.cs](/mnt/a/Develop/github/sgr/src/Alkampfer.Sgr.Playground/PlaygroundTelemetry.cs)

## Shared Telemetry Surface In The Library

The reusable library defines a single shared telemetry entry point in `SgrTelemetry`.

### ActivitySource And Meter

```csharp
public const string ActivitySourceName = "Alkampfer.Sgr";
public const string MeterName = "Alkampfer.Sgr";

public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
public static readonly Meter Meter = new(MeterName);
```

Source: [src/Alkampfer.Sgr/Telemetry/SgrTelemetry.cs](/mnt/a/Develop/github/sgr/src/Alkampfer.Sgr/Telemetry/SgrTelemetry.cs)

Because the playground registers `.AddSource(SgrTelemetry.ActivitySourceName)` and `.AddMeter(SgrTelemetry.MeterName)`, every activity and metric created through `SgrTelemetry` is exported automatically.

### Custom Metrics

The library publishes counters and histograms for:

- started, completed, and failed conversations
- active conversations
- conversation duration
- LLM call count, failures, and duration
- token usage for input, output, and total tokens
- tool call count, failures, and duration

These metrics are created in [src/Alkampfer.Sgr/Telemetry/SgrTelemetry.cs](/mnt/a/Develop/github/sgr/src/Alkampfer.Sgr/Telemetry/SgrTelemetry.cs).

## How A Trace Starts

The scenario root span is created in the playground before executing a sample.

```csharp
using var scenarioActivity = SgrTelemetry.StartScenarioActivity(
    scenarioName,
    reasonerMode,
    userRequest);

var traceId = scenarioActivity?.TraceId.ToString();
AnsiConsole.MarkupLine($"[grey]TraceId:[/] {traceId ?? "not available"}");
logger?.LogInformation("Starting scenario {ScenarioName} with trace id {TraceId}", scenarioName, traceId);
```

Source: [src/Alkampfer.Sgr.Playground/Program.cs#L617](/mnt/a/Develop/github/sgr/src/Alkampfer.Sgr.Playground/Program.cs#L617)

This gives each scenario a root span and also prints the trace id so you can find the same execution in:

- the terminal
- exported OTLP logs
- Aspire traces

## How Reasoner Spans Are Created

The normal reasoner creates:

- one execution span for the whole reasoning process
- one planning-step span per iteration
- one model-call span for each OpenAI request

Example from the normal Response API reasoner:

```csharp
using var executionActivity = SgrTelemetry.StartReasonerExecution("response-api", userRequest);

for (int step = 1; step <= 20; step++)
{
    using var stepActivity = SgrTelemetry.StartPlanningStep("response-api", step);

    modelActivity = SgrTelemetry.StartModelCall(
        provider: ResponseApiProvider,
        model: _deploymentId,
        systemPrompt: systemPrompt,
        userPrompt: dumpAllPrompt,
        schema: schemaStr);

    SgrTelemetry.RecordLlmCallStarted(ResponseApiProvider, _deploymentId);
}
```

Source: [src/Alkampfer.Sgr/Runtime/ResponseApiSchemaGuidedReasoner.cs](/mnt/a/Develop/github/sgr/src/Alkampfer.Sgr/Runtime/ResponseApiSchemaGuidedReasoner.cs)

The forced-tool reasoner follows the same pattern, but it emits model spans for both phases of each step:

- planning phase
- tool-parameter resolution phase

Source: [src/Alkampfer.Sgr/Runtime/ResponseApiForcedToolSchemaGuidedReasoner.cs](/mnt/a/Develop/github/sgr/src/Alkampfer.Sgr/Runtime/ResponseApiForcedToolSchemaGuidedReasoner.cs)

## How Tool Execution Is Traced

Business tool execution is wrapped in its own span in `BusinessFunctionFactory`.

```csharp
using var toolActivity = SgrTelemetry.StartToolCall(toolName, toolCall);
toolActivity?.SetTag("sgr.tool.handler", businessFunction.GetType().Name);

var result = await businessFunction.ExecuteAsync(toolCall, cancellationToken).ConfigureAwait(false);

SgrTelemetry.RecordToolResult(toolActivity, result.Summary);
SgrTelemetry.RecordToolCompleted(toolActivity, toolName, stopwatch.Elapsed);
```

Source: [src/Alkampfer.Sgr/Runtime/BusinessFunctionFactory.cs#L277](/mnt/a/Develop/github/sgr/src/Alkampfer.Sgr/Runtime/BusinessFunctionFactory.cs#L277)

This means a trace shows not only that the model asked for a tool, but also:

- which tool type was dispatched
- which concrete handler executed it
- the serialized parameters
- the summary returned by the tool
- whether the tool failed

## What Gets Stored On Spans

The project records rich tags on spans for local debugging.

### Scenario Span

Tags include:

- `sgr.scenario.name`
- `sgr.reasoner.mode`
- `sgr.user.request`

### Model Call Span

Tags include:

- `gen_ai.system`
- `gen_ai.request.model`
- `sgr.prompt.system`
- `sgr.prompt.user`
- `sgr.prompt.schema`
- `sgr.response.content`
- `sgr.usage.input_tokens`
- `sgr.usage.output_tokens`
- `sgr.usage.total_tokens`

### Tool Span

Tags include:

- `sgr.tool.name`
- `sgr.tool.parameters`
- `sgr.tool.handler`
- `sgr.tool.summary`

Errors are attached through:

- `exception.type`
- `exception.message`
- activity status set to `Error`

## Log Correlation

The playground enables `ActivityTrackingOptions.TraceId`, `SpanId`, and `ParentId` on `ILogger`, so logs emitted while an activity is active are correlated with the current trace.

That means logs from:

- `Program`
- the reasoners
- `BusinessFunctionFactory`

can be matched back to the same scenario and span tree inside the dashboard.

## Automatic HTTP And SQL Instrumentation

The playground adds:

- `.AddHttpClientInstrumentation()`
- `.AddSqlClientInstrumentation()`

So, in addition to custom SGR spans, you also get automatic spans for:

- outbound HTTP requests
- SQL Server calls performed through `SqlClient`

Those spans appear under the current scenario trace whenever they happen during a reasoning run.

## Running The Playground With Telemetry

### Required Environment

The playground loads `.env` files by walking up parent folders and expects:

- `OPENAI_API_KEY`
- `AZURE_ENDPOINT`

Optional:

- `OTEL_EXPORTER_OTLP_ENDPOINT`

Example:

```env
OPENAI_API_KEY=your-key
AZURE_ENDPOINT=https://your-resource.openai.azure.com/
OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317
```

### Start The Playground

```bash
dotnet run --project src/Alkampfer.Sgr.Playground/Alkampfer.Sgr.Playground.csproj
```

At startup, the playground prints the active OTLP endpoint. When a scenario runs, it also prints the trace id so you can immediately locate the execution in the dashboard.

## End-To-End Flow

The full telemetry flow in this repository is:

1. `Program` creates `PlaygroundTelemetry`.
2. `PlaygroundTelemetry` configures OpenTelemetry logs, traces, metrics, and OTLP exporters.
3. `Program` starts a root scenario activity.
4. The reasoner creates nested execution, step, and model-call spans.
5. `BusinessFunctionFactory` creates tool spans around business function dispatch.
6. Automatic HTTP and SQL instrumentation adds dependency spans where applicable.
7. The OTLP exporter sends everything to the configured OTLP backend, such as the Aspire dashboard.

## Example: Minimal Pattern Used In This Repo

If you want to follow the same pattern in another host, this is the essence:

```csharp
using var root = SgrTelemetry.StartScenarioActivity(
    scenarioName: "Simple Email Task",
    reasonerMode: "response-api",
    userRequest: "Send an email to john@example.com");

using var execution = SgrTelemetry.StartReasonerExecution("response-api", "Send an email to john@example.com");
using var step = SgrTelemetry.StartPlanningStep("response-api", 1);

var modelSpan = SgrTelemetry.StartModelCall(
    provider: "azure-openai-response-api",
    model: "gpt-5-mini",
    systemPrompt: systemPrompt,
    userPrompt: userPrompt,
    schema: jsonSchema);

try
{
    SgrTelemetry.RecordLlmCallStarted("azure-openai-response-api", "gpt-5-mini");

    // Call Azure OpenAI here

    SgrTelemetry.RecordModelResponse(modelSpan, assistantResponse);
    SgrTelemetry.RecordUsage(modelSpan, inputTokens, outputTokens, totalTokens);
    SgrTelemetry.RecordLlmCallCompleted(modelSpan, elapsed);
}
catch (Exception ex)
{
    SgrTelemetry.RecordLlmCallFailed(modelSpan, elapsed);
    SgrTelemetry.MarkError(modelSpan, ex);
    throw;
}
finally
{
    modelSpan?.Dispose();
}
```

## Example: Adding A New Instrumented Tool

New business functions automatically fit the tracing model as long as they are dispatched through `BusinessFunctionFactory`.

The relevant pattern is:

```csharp
using var toolActivity = SgrTelemetry.StartToolCall(toolName, toolCall);

try
{
    var result = await businessFunction.ExecuteAsync(toolCall, cancellationToken);
    SgrTelemetry.RecordToolResult(toolActivity, result.Summary);
    SgrTelemetry.RecordToolCompleted(toolActivity, toolName, stopwatch.Elapsed);
}
catch (Exception ex)
{
    SgrTelemetry.RecordToolFailed(toolActivity, toolName, stopwatch.Elapsed);
    SgrTelemetry.MarkError(toolActivity, ex);
    throw;
}
```

If you add a new tool and route it through the existing factory, it will appear automatically in the same trace tree.

## Practical Notes

- Prompt and response content are intentionally attached to spans for local debugging.
- Treat dashboard-based telemetry as development tooling because traces can contain sensitive prompt data.
- The library itself does not force any exporter choice; only the playground wires OTLP export.
- If you build another host around `Alkampfer.Sgr`, you can reuse `SgrTelemetry` and configure exporters differently.

## Verification Commands

Build the solution:

```bash
dotnet build sgr.slnx -c Debug
```

Run tests:

```bash
dotnet test sgr.slnx -c Debug
```

Run the playground:

```bash
dotnet run --project src/Alkampfer.Sgr.Playground/Alkampfer.Sgr.Playground.csproj
```

## References

- [src/Alkampfer.Sgr.Playground/PlaygroundTelemetry.cs](/mnt/a/Develop/github/sgr/src/Alkampfer.Sgr.Playground/PlaygroundTelemetry.cs)
- [src/Alkampfer.Sgr.Playground/Program.cs](/mnt/a/Develop/github/sgr/src/Alkampfer.Sgr.Playground/Program.cs)
- [src/Alkampfer.Sgr/Telemetry/SgrTelemetry.cs](/mnt/a/Develop/github/sgr/src/Alkampfer.Sgr/Telemetry/SgrTelemetry.cs)
- [src/Alkampfer.Sgr/Runtime/ResponseApiSchemaGuidedReasoner.cs](/mnt/a/Develop/github/sgr/src/Alkampfer.Sgr/Runtime/ResponseApiSchemaGuidedReasoner.cs)
- [src/Alkampfer.Sgr/Runtime/ResponseApiForcedToolSchemaGuidedReasoner.cs](/mnt/a/Develop/github/sgr/src/Alkampfer.Sgr/Runtime/ResponseApiForcedToolSchemaGuidedReasoner.cs)
- [src/Alkampfer.Sgr/Runtime/BusinessFunctionFactory.cs](/mnt/a/Develop/github/sgr/src/Alkampfer.Sgr/Runtime/BusinessFunctionFactory.cs)
- [ASPIRE.md](/mnt/a/Develop/github/sgr/ASPIRE.md)
- [Aspire dashboard overview](https://learn.microsoft.com/en-us/dotnet/aspire/fundamentals/dashboard/overview)
- [OpenTelemetry .NET log correlation](https://opentelemetry.io/docs/languages/dotnet/logs/correlation/)
