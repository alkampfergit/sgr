using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Alkampfer.Sgr.BusinessFunctions;
using Alkampfer.Sgr.Models;
using Alkampfer.Sgr.Services;
using Alkampfer.Sgr.Utils;
using Microsoft.Extensions.Logging;

namespace Alkampfer.Sgr.Runtime;

/// <summary>
/// Shared utilities and defaults for schema-guided reasoning implementations.
/// </summary>
public static class SchemaGuidedReasonerSupport
{
    public static string GenerateToolsSummary(ToolInformation[] availableTools)
    {
        var summary = new System.Text.StringBuilder();

        foreach (var tool in availableTools)
        {
            summary.AppendLine($"- **{tool.ToolName}**: {tool.ToolDescription}");
        }

        return summary.ToString();
    }
}

/// <summary>
/// Configuration options for schema-guided reasoners.
/// </summary>
public sealed class SchemaGuidedReasonerOptions
{
    public Func<SchemaGuidedReasonerPromptContext, IEnumerable<Type>?, string> SystemPromptBuilder { get; init; } = (context, _) =>
    {
        var toolsSummary = SchemaGuidedReasonerSupport.GenerateToolsSummary(context.Schema.AvailableTools);
        return $@"You are an AI assistant. Always respond with structured JSON that matches the provided schema.

## Available Tools:
{toolsSummary}";
    };

    public Func<string, IReadOnlyList<SchemaGuidedReasonerToolHistory>, IEnumerable<Type>?>? ToolTypeSelector { get; init; }

    public JsonSerializerOptions? JsonSerializerOptions { get; init; }

    public object? CustomContext { get; init; }

    public static SchemaGuidedReasonerOptions CreateDefault() => new()
    {
        JsonSerializerOptions = CreateDefaultJsonOptions()
    };

    public static JsonSerializerOptions CreateDefaultJsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        TypeInfoResolver = System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault
            ? new DefaultJsonTypeInfoResolver()
            : JsonTypeInfoResolver.Combine()
    };
}

public readonly record struct SchemaGuidedReasonerPromptContext(
    SchemaGenerationResult Schema,
    BusinessFunctionFactory FunctionFactory,
    object? CustomContext);

public readonly record struct SchemaGuidedReasonerToolHistory(string ToolName, string Summary);

/// <summary>
/// Factory class encapsulating default configuration for schema-guided reasoners.
/// </summary>
public static class SchemaGuidedReasonerFactory
{
    public static BusinessFunctionFactory CreateDefaultBusinessFunctionFactory(
        DatabaseService databaseService,
        AzureOpenAiConfiguration openAiConfiguration,
        ILoggerFactory loggerFactory,
        SqlServerService? sqlServerService = null)
    {
        if (databaseService is null) throw new ArgumentNullException(nameof(databaseService));
        if (openAiConfiguration is null) throw new ArgumentNullException(nameof(openAiConfiguration));
        if (loggerFactory is null) throw new ArgumentNullException(nameof(loggerFactory));

        sqlServerService ??= new SqlServerService();
        var toolList = new Type[]
        {
            typeof(ReportTaskCompletionToolCall),
            typeof(SendEmailToolCall),
            typeof(IssueInvoiceToolCall),
            typeof(GetCustomerDataToolCall),
            typeof(VoidInvoiceToolCall),
            typeof(CreateRuleToolCall),
        };

        return new BusinessFunctionFactory(databaseService, sqlServerService, openAiConfiguration, loggerFactory, toolList);
    }

    public static SchemaGuidedReasonerOptions CreateDefaultOptions(DatabaseService databaseService)
    {
        if (databaseService is null) throw new ArgumentNullException(nameof(databaseService));

        var jsonOptions = SchemaGuidedReasonerOptions.CreateDefaultJsonOptions();

        var metadata = new DefaultPromptMetadata(
            Persona: "You are a business assistant helping Rinat Abdullin with customer interactions.",
            Guidelines: new[]
            {
                "Clearly report when tasks are done using report_task_completion",
                "Always send customers emails after issuing invoices (with invoice attached)",
                "Be laconic. Especially in emails",
                "No need to wait for payment confirmation before proceeding",
                "Always check customer data before issuing invoices or making changes",
                "When you determine that there is nothing to do anymore use the report_task_completion tool",
                "Try to send the email as last step after all other tasks are done"
            },
            ProductCatalogJson: databaseService.GetProductCatalogAsJson(jsonOptions));

        return new SchemaGuidedReasonerOptions
        {
            JsonSerializerOptions = jsonOptions,
            CustomContext = metadata,
            SystemPromptBuilder = (context, _) => BuildDefaultPrompt(context, metadata)
        };
    }

    private static string BuildDefaultPrompt(
        SchemaGuidedReasonerPromptContext context,
        DefaultPromptMetadata metadata)
    {
        var toolsSummary = SchemaGuidedReasonerSupport.GenerateToolsSummary(context.Schema.AvailableTools);
        var guidelines = string.Join(Environment.NewLine, metadata.Guidelines.Select(g => $"- {g}"));

        return $@"
{metadata.Persona}

IMPORTANT: You must always respond with structured JSON that includes:
1. Current state analysis
2. List of remaining steps briefly described and include corresponding tool if applicable
3. Whether the task is completed
4. The specific tool call to execute next
5. The tool call to execute next is that one that logically follows from the current state and remaining steps

## Available Tools:
{toolsSummary}

Guidelines:
{guidelines}

Products: {metadata.ProductCatalogJson}";
    }

    private sealed record DefaultPromptMetadata(
        string Persona,
        IReadOnlyList<string> Guidelines,
        string ProductCatalogJson);
}
