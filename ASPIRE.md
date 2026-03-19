# Aspire Dashboard Integration

This document explains how the project integrates with the Aspire dashboard for local observability.

## Integration Model

This repository does not contain an Aspire app host or service-defaults project.

Instead, the integration is intentionally lightweight:

- the shared library emits custom OpenTelemetry spans and metrics
- the playground configures OTLP exporters
- the Aspire dashboard runs separately and receives OTLP telemetry from the playground

In other words, Aspire is used here as a standalone dashboard and trace explorer rather than as the application host.

## Where The Integration Lives

- [src/Alkampfer.Sgr.Playground/PlaygroundTelemetry.cs](/mnt/a/Develop/github/sgr/src/Alkampfer.Sgr.Playground/PlaygroundTelemetry.cs)
  Configures OpenTelemetry logs, traces, metrics, and OTLP export.
- [src/Alkampfer.Sgr.Playground/Program.cs](/mnt/a/Develop/github/sgr/src/Alkampfer.Sgr.Playground/Program.cs)
  Prints the active OTLP endpoint and the trace id for each scenario.
- [src/Alkampfer.Sgr/Telemetry/SgrTelemetry.cs](/mnt/a/Develop/github/sgr/src/Alkampfer.Sgr/Telemetry/SgrTelemetry.cs)
  Defines the custom `ActivitySource`, `Meter`, spans, and metrics consumed by the playground exporter setup.

## How Telemetry Reaches Aspire

The playground reads the OTLP endpoint from `OTEL_EXPORTER_OTLP_ENDPOINT`.

If the variable is missing, it defaults to:

```text
http://localhost:4317
```

That default is defined in [src/Alkampfer.Sgr.Playground/PlaygroundTelemetry.cs](/mnt/a/Develop/github/sgr/src/Alkampfer.Sgr.Playground/PlaygroundTelemetry.cs).

The playground exports three telemetry signals to that endpoint:

- traces
- metrics
- logs

## Exporter Configuration

The Aspire-compatible wiring happens through the OTLP exporters configured by the playground:

```csharp
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

## Start The Aspire Dashboard

Run the standalone dashboard container:

```bash
docker run -it -d -p 18888:18888 -p 4317:18889 --name aspire-dashboard mcr.microsoft.com/dotnet/aspire-dashboard:latest
```

Then read the container logs to get the access token:

```bash
docker logs aspire-dashboard
```

Open the dashboard at:

- `http://localhost:18888`

## Port Mapping Explained

The container mapping is set up so the project can keep using the conventional local OTLP gRPC endpoint:

- host `18888` -> container `18888` for the dashboard UI
- host `4317` -> container `18889` for Aspire OTLP gRPC ingestion

Because of that, the playground default `http://localhost:4317` works without extra configuration.

## Run The Playground Against Aspire

If you want to be explicit, set:

```bash
export OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317
```

Then start the playground:

```bash
dotnet run --project src/Alkampfer.Sgr.Playground/Alkampfer.Sgr.Playground.csproj
```

At startup, the playground prints:

- the OTLP endpoint it is using
- the trace id for each executed scenario

That makes it easy to find the same execution inside Aspire.

## What You Will See In Aspire

A typical scenario produces:

- a root `sgr.scenario` span
- nested `sgr.reasoner.execute` and `sgr.reasoner.step` spans
- `sgr.model.call` spans for Azure OpenAI requests
- `sgr.tool.execute` spans for dispatched business functions
- automatic HTTP spans
- automatic SQL spans when `SqlClient` is used
- correlated logs that carry trace and span ids
- custom metrics published from `SgrTelemetry`

## Example Trace Root

The scenario root activity is started in the playground like this:

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

This trace id is the easiest bridge between the terminal output and the Aspire dashboard UI.

## Example Model Span Data

The reasoner records prompt, schema, response, and token information on model spans:

```csharp
modelActivity = SgrTelemetry.StartModelCall(
    provider: ResponseApiProvider,
    model: _deploymentId,
    systemPrompt: systemPrompt,
    userPrompt: dumpAllPrompt,
    schema: schemaStr);

SgrTelemetry.RecordLlmCallStarted(ResponseApiProvider, _deploymentId);
SgrTelemetry.RecordModelResponse(modelActivity, assistantRaw);
SgrTelemetry.RecordUsage(
    modelActivity,
    usage.InputTokenCount,
    usage.OutputTokenCount,
    usage.TotalTokenCount);
SgrTelemetry.RecordLlmCallCompleted(modelActivity, llmStopwatch.Elapsed);
```

Source: [src/Alkampfer.Sgr/Runtime/ResponseApiSchemaGuidedReasoner.cs](/mnt/a/Develop/github/sgr/src/Alkampfer.Sgr/Runtime/ResponseApiSchemaGuidedReasoner.cs)

Inside Aspire, this lets you inspect a model call together with:

- the prompt payload
- the generated schema
- the assistant response
- token usage
- timing

## Example Tool Span Data

Tool execution is also visible in Aspire:

```csharp
using var toolActivity = SgrTelemetry.StartToolCall(toolName, toolCall);
toolActivity?.SetTag("sgr.tool.handler", businessFunction.GetType().Name);

var result = await businessFunction.ExecuteAsync(toolCall, cancellationToken).ConfigureAwait(false);

SgrTelemetry.RecordToolResult(toolActivity, result.Summary);
SgrTelemetry.RecordToolCompleted(toolActivity, toolName, stopwatch.Elapsed);
```

Source: [src/Alkampfer.Sgr/Runtime/BusinessFunctionFactory.cs#L277](/mnt/a/Develop/github/sgr/src/Alkampfer.Sgr/Runtime/BusinessFunctionFactory.cs#L277)

That makes Aspire useful for understanding not only what the model planned, but what the tool layer actually did.

## Notes

- Prompt and response content are exported for local debugging, so avoid treating the dashboard as a production-safe sink by default.
- The playground uses OTLP over gRPC.
- If you want to send telemetry somewhere other than Aspire, set `OTEL_EXPORTER_OTLP_ENDPOINT` to another compatible OTLP collector.

## References

- [README.md](/mnt/a/Develop/github/sgr/README.md)
- [src/Alkampfer.Sgr.Playground/PlaygroundTelemetry.cs](/mnt/a/Develop/github/sgr/src/Alkampfer.Sgr.Playground/PlaygroundTelemetry.cs)
- [src/Alkampfer.Sgr.Playground/Program.cs](/mnt/a/Develop/github/sgr/src/Alkampfer.Sgr.Playground/Program.cs)
- [src/Alkampfer.Sgr/Telemetry/SgrTelemetry.cs](/mnt/a/Develop/github/sgr/src/Alkampfer.Sgr/Telemetry/SgrTelemetry.cs)
- [src/Alkampfer.Sgr/Runtime/ResponseApiSchemaGuidedReasoner.cs](/mnt/a/Develop/github/sgr/src/Alkampfer.Sgr/Runtime/ResponseApiSchemaGuidedReasoner.cs)
- [src/Alkampfer.Sgr/Runtime/BusinessFunctionFactory.cs](/mnt/a/Develop/github/sgr/src/Alkampfer.Sgr/Runtime/BusinessFunctionFactory.cs)
- [Aspire dashboard overview](https://learn.microsoft.com/en-us/dotnet/aspire/fundamentals/dashboard/overview)
