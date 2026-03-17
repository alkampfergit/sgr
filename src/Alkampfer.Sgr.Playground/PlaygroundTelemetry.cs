using System.Diagnostics;
using Alkampfer.Sgr.Telemetry;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Alkampfer.Sgr.Playground;

internal sealed class PlaygroundTelemetry : IDisposable
{
    private const string DefaultOtlpEndpoint = "http://localhost:4317";
    private const string ServiceName = "Alkampfer.Sgr.Playground";

    private readonly MeterProvider _meterProvider;
    private readonly TracerProvider _tracerProvider;

    private PlaygroundTelemetry(
        ILoggerFactory loggerFactory,
        TracerProvider tracerProvider,
        MeterProvider meterProvider,
        string otlpEndpoint)
    {
        LoggerFactory = loggerFactory;
        _tracerProvider = tracerProvider;
        _meterProvider = meterProvider;
        OtlpEndpoint = otlpEndpoint;
    }

    public ILoggerFactory LoggerFactory { get; }

    public string OtlpEndpoint { get; }

    public static PlaygroundTelemetry Create()
    {
        Activity.DefaultIdFormat = ActivityIdFormat.W3C;
        Activity.ForceDefaultIdFormat = true;

        var endpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            endpoint = DefaultOtlpEndpoint;
        }

        var serviceVersion = typeof(PlaygroundTelemetry).Assembly.GetName().Version?.ToString();
        var resourceBuilder = ResourceBuilder.CreateDefault()
            .AddService(ServiceName, serviceVersion: serviceVersion);

        var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddFilter("Microsoft.Extensions.Http", LogLevel.Warning);
            builder.Configure(options =>
            {
                options.ActivityTrackingOptions =
                    ActivityTrackingOptions.TraceId |
                    ActivityTrackingOptions.SpanId |
                    ActivityTrackingOptions.ParentId;
            });
            builder.AddSimpleConsole(options =>
            {
                options.IncludeScopes = true;
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss ";
            });
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

        return new PlaygroundTelemetry(loggerFactory, tracerProvider, meterProvider, endpoint);
    }

    public void Dispose()
    {
        _meterProvider.Dispose();
        _tracerProvider.Dispose();
        LoggerFactory.Dispose();
    }
}
