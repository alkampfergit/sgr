# Alkampfer.Sgr

`Alkampfer.Sgr` is a schema-guided reasoning library with a console playground that can run end-to-end SGR scenarios against Azure OpenAI and optional SQL Server workflows.

## Logging Analysis

Before the OpenTelemetry work, the solution had a minimal logging setup:

- The playground and tests registered `Microsoft.Extensions.Logging` with a console provider.
- Only a few business functions emitted structured logs through `ILogger<T>`.
- The two reasoners mostly wrote directly to `Spectre.Console`, so prompt/response flow was visible in the terminal but not exported as structured telemetry.
- There was no shared `ActivitySource`, no OTLP exporter, and no guaranteed scenario-wide trace correlation.

That meant logs, model prompts, model responses, and tool execution were hard to correlate across a full SGR execution.

## OpenTelemetry Plan And Current Shape

The solution now follows this tracing model:

- A single root `Activity` is created for each playground scenario.
- Nested activities are created for reasoner execution, each reasoning step, every model call, and every business tool dispatch.
- Model system prompts, user prompts, schemas, and model responses are attached to the model-call span so an entire SGR scenario can be reconstructed from one trace.
- `Activity.Current` is used as the ambient correlation context, so trace context flows through `async` calls automatically.
- Playground logging enables `ActivityTrackingOptions.TraceId`, `SpanId`, and `ParentId`, which makes the active trace visible in console logs when present.
- The playground exports traces and logs to an OTLP endpoint, defaulting to `http://localhost:4317`.
- `HttpClient` and `SqlClient` instrumentation are enabled in the playground so outbound HTTP and SQL work can appear under the same trace.

## Trace Correlation Rules

The correlation strategy is intentionally simple:

- Start one scenario `Activity` at the beginning of each playground example.
- Keep all nested LLM calls and tool execution inside that activity tree.
- Let `Activity.Current` flow through the async pipeline.
- Let OpenTelemetry log correlation populate `TraceId` and `SpanId` from the active activity.

This gives one trace id per SGR scenario, while still preserving nested spans for planning steps, model calls, and tool execution.

## Run The Aspire Dashboard

The playground is configured to export OTLP telemetry to `http://localhost:4317` by default. Start the standalone Aspire dashboard container first:

```bash
docker run --rm -it -d -p 18888:18888 -p 4317:18889 --name aspire-dashboard  mcr.microsoft.com/dotnet/aspire-dashboard:latest
```

Then fetch the dashboard login details:

```bash
docker logs aspire-dashboard
```

Open `http://localhost:18888` in your browser and use the token shown in the container logs.

Notes:

- Host port `4317` is mapped to the dashboard OTLP gRPC endpoint.
- The playground uses that host port by default through `OTEL_EXPORTER_OTLP_ENDPOINT`.
- If you want a different endpoint, set `OTEL_EXPORTER_OTLP_ENDPOINT` before starting the playground.

## Run The Playground

With the dashboard running:

```bash
dotnet run --project src/Alkampfer.Sgr.Playground/Alkampfer.Sgr.Playground.csproj
```

When a scenario starts, the playground prints the active trace id in the console. Use that id to jump between:

- terminal logs
- exported OpenTelemetry logs
- Aspire trace details
- nested spans for model calls and tool execution

## What Is Traced

For each SGR scenario, the telemetry now captures:

- scenario root span
- reasoner execution span
- individual reasoning step spans
- model-call spans with system prompt, user prompt, schema, and assistant response
- tool execution spans with serialized tool input and tool result summary
- correlated logs with trace/span ids when an activity is active

## Development Notes

- Prompt and response bodies are intentionally attached to telemetry for local debugging.
- Treat the Aspire dashboard as development tooling because traces can include sensitive prompt content.
- The standalone Aspire dashboard Docker flow is documented by Microsoft Learn:
  [Aspire dashboard overview](https://learn.microsoft.com/en-us/dotnet/aspire/fundamentals/dashboard/overview)
- Log correlation is based on `Activity.Current`, which OpenTelemetry .NET uses to populate `TraceId` and `SpanId` on log records:
  [OpenTelemetry .NET log correlation](https://opentelemetry.io/docs/languages/dotnet/logs/correlation/)
