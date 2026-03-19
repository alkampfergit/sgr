using Azure;
using Azure.AI.OpenAI;
using Newtonsoft.Json;
using OpenAI.Responses;
using Alkampfer.Sgr.Models;
using Alkampfer.Sgr.BusinessFunctions;
using Alkampfer.Sgr.Telemetry;
using Alkampfer.Sgr.Utils;
using Spectre.Console;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using System.Text;

namespace Alkampfer.Sgr.Runtime;

#pragma warning disable OPENAI001

/// <summary>
/// Schema-guided reasoner that uses a two-phase strategy for each step:
/// first it performs a structured planning call,
/// then it takes the first planned step, reads the explicit tool id to execute, and
/// asks the model for only that tool's parameter object.
/// </summary>
public class ResponseApiForcedToolSchemaGuidedReasoner
{
    private const string ResponseApiProvider = "azure-openai-response-api";

    private readonly string _azureEndpoint;
    private readonly string _azureApiKey;
    private readonly string _deploymentId;
    private readonly BusinessFunctionFactory _functionFactory;
    private readonly SchemaGuidedReasonerOptions _options;
    private readonly ILogger<ResponseApiForcedToolSchemaGuidedReasoner> _logger;

    public bool VerboseOutput { get; set; } = true;

    public ResponseReasoningEffortLevel ReasoningEffortLevel { get; set; } = ResponseReasoningEffortLevel.Medium;

    public ResponseApiSchemaGuidedReasoner.TokenUsageStats CurrentSessionStats { get; private set; } = new();

    private int _llmCallCounter = 0;
    private readonly string _llmCallsDirectory = Path.Combine(Directory.GetCurrentDirectory(), "llm_calls");

    private sealed record ToolExecutionResult(string ToolName, string Parameters, string Summary);

    public ResponseApiForcedToolSchemaGuidedReasoner(
        string azureEndpoint,
        string azureApiKey,
        string deploymentId,
        BusinessFunctionFactory businessFunctionFactory,
        SchemaGuidedReasonerOptions? options = null,
        ILogger<ResponseApiForcedToolSchemaGuidedReasoner>? logger = null)
    {
        _azureEndpoint = azureEndpoint ?? throw new ArgumentNullException(nameof(azureEndpoint));
        _azureApiKey = azureApiKey ?? throw new ArgumentNullException(nameof(azureApiKey));
        _deploymentId = deploymentId ?? throw new ArgumentNullException(nameof(deploymentId));
        _functionFactory = businessFunctionFactory ?? throw new ArgumentNullException(nameof(businessFunctionFactory));
        _options = options ?? SchemaGuidedReasonerOptions.CreateDefault();
        _logger = logger ?? NullLogger<ResponseApiForcedToolSchemaGuidedReasoner>.Instance;
    }

    public async Task<string> ReasonAndActAsync(string userRequest)
    {
        const string reasonerKind = "response-api-forced-tool";
        using var executionActivity = SgrTelemetry.StartReasonerExecution(reasonerKind, userRequest);

        CurrentSessionStats = new ResponseApiSchemaGuidedReasoner.TokenUsageStats();
        _llmCallCounter = 0;
        EnsureLlmCallsDirectory();

        var executionTaskResult = new List<ToolExecutionResult>();

        var clientOptions = new AzureOpenAIClientOptions(
            AzureOpenAIClientOptions.ServiceVersion.V2025_04_01_Preview);

        var client = new AzureOpenAIClient(
            new Uri(_azureEndpoint),
            new AzureKeyCredential(_azureApiKey),
            clientOptions);

        var responseClient = client.GetOpenAIResponseClient(_deploymentId);
        _logger.LogInformation("Starting two-phase Response API SGR execution.");

        for (int step = 1; step <= 20; step++)
        {
            using var stepActivity = SgrTelemetry.StartPlanningStep(reasonerKind, step);
            stepActivity?.SetTag("sgr.executed_task_count", executionTaskResult.Count);

            if (VerboseOutput)
            {
                AnsiConsole.Write($"[yellow]Planning step_{step}...[/] ");
                AnsiConsole.MarkupLine($"[grey](Two-phase OpenAI API call pair for step {step})[/]");
            }

            try
            {
                var availableToolTypes = DetermineAvailableTools(userRequest, executionTaskResult);
                var systemPrompt = GenerateSystemPrompt(availableToolTypes);
                var planningInputItems = BuildInputItems(userRequest, executionTaskResult, systemPrompt);

                var planningSchemaManager = CreateForcedPlanningSchemaManager(availableToolTypes);
                var planningSchema = availableToolTypes != null
                    ? planningSchemaManager.GenerateSchema(availableToolTypes)
                    : planningSchemaManager.GenerateSchema();

                var plannedStep = await RequestNextStepAsync(
                    responseClient,
                    planningInputItems,
                    planningSchemaManager,
                    planningSchema,
                    "plan").ConfigureAwait(false);

                DisplayPlannedStep(plannedStep);

                if (TryCompleteFromPlannedStep(plannedStep, out var planningCompletionSummary))
                {
                    return CompleteReasoning("from the first planning call", planningCompletionSummary);
                }

                var selectedTool = await ResolveSelectedToolAsync(
                    responseClient,
                    userRequest,
                    executionTaskResult,
                    systemPrompt,
                    availableToolTypes,
                    plannedStep).ConfigureAwait(false);

                if (selectedTool is ReportTaskCompletionToolCall completionParameter)
                {
                    return CompleteReasoning(string.Empty, completionParameter.Summary);
                }

                await ExecuteSelectedToolAsync(selectedTool, executionTaskResult).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SgrTelemetry.MarkError(stepActivity, ex);
                SgrTelemetry.MarkError(executionActivity, ex);
                _logger.LogError(ex, "Forced-tool Response API reasoning failed at step {Step}", step);
                return ReportReasoningFailure(step, ex);
            }
        }

        DisplaySessionTokenUsageSummary();

        return "Task completed after maximum reasoning steps";
    }

    private string GenerateSystemPrompt(IEnumerable<Type>? availableToolTypes = null)
    {
        var schemaResult = availableToolTypes != null
            ? _functionFactory.GenerateSchemaWithDocumentationForToolCall(availableToolTypes)
            : _functionFactory.GenerateSchemaWithDocumentationForToolCall();

        var context = new SchemaGuidedReasonerPromptContext(
            schemaResult,
            _functionFactory,
            _options.CustomContext);

        return _options.SystemPromptBuilder(context, availableToolTypes);
    }

    private IEnumerable<Type>? DetermineAvailableTools(string userRequest, List<ToolExecutionResult> executedTasks)
    {
        if (_options.ToolTypeSelector is null)
        {
            return null;
        }

        var history = executedTasks
            .Select(t => new SchemaGuidedReasonerToolHistory(t.ToolName, t.Summary))
            .ToList()
            .AsReadOnly();

        return _options.ToolTypeSelector(userRequest, history);
    }

    private static List<ResponseItem> BuildInputItems(
        string userRequest,
        List<ToolExecutionResult> executionTaskResult,
        string systemPrompt)
    {
        var inputItems = new List<ResponseItem>
        {
            ResponseItem.CreateSystemMessageItem(systemPrompt),
            ResponseItem.CreateUserMessageItem($"UserQuestion: {userRequest}"),
        };

        if (executionTaskResult.Count > 0)
        {
            inputItems.Add(ResponseItem.CreateAssistantMessageItem("Function called so far:\n"));
        }

        for (int i = 0; i < executionTaskResult.Count; i++)
        {
            ToolExecutionResult toolCallResponse = executionTaskResult[i];
            var callId = $"tool_call_{i}";
            inputItems.Add(ResponseItem.CreateFunctionCallItem(
                callId,
                toolCallResponse.ToolName,
                BinaryData.FromString(toolCallResponse.Parameters)));
            inputItems.Add(ResponseItem.CreateFunctionCallOutputItem(callId, toolCallResponse.Summary));
        }

        return inputItems;
    }

    private async Task<ForcedReasonerNextStep> RequestNextStepAsync(
        OpenAIResponseClient responseClient,
        List<ResponseItem> inputItems,
        PolymorphicSchemaManager<ForcedReasonerNextStep, ToolCall> planningSchemaManager,
        string schema,
        string phaseName)
    {
        Stopwatch? llmStopwatch = null;
        Activity? modelActivity = null;

        try
        {
            var dumpAllPrompt = Dump(inputItems);

            _llmCallCounter++;

            var options = new ResponseCreationOptions
            {
                PreviousResponseId = null,
                ReasoningOptions = new ResponseReasoningOptions()
                {
                    ReasoningEffortLevel = ReasoningEffortLevel
                },
                TextOptions = new ResponseTextOptions
                {
                    TextFormat = ResponseTextFormat.CreateJsonSchemaFormat(
                        jsonSchemaFormatName: "ForcedReasonerNextStep",
                        jsonSchema: BinaryData.FromString(schema),
                        jsonSchemaFormatDescription: "Schema for structured planning with polymorphic ToolCall support",
                        jsonSchemaIsStrict: true)
                },
            };

            modelActivity = SgrTelemetry.StartModelCall(
                provider: ResponseApiProvider,
                model: $"{_deploymentId}:{phaseName}",
                systemPrompt: inputItems.OfType<MessageResponseItem>().FirstOrDefault(m => m.Role == MessageRole.System)?.Content.FirstOrDefault()?.Text ?? string.Empty,
                userPrompt: dumpAllPrompt,
                schema: schema);
            SgrTelemetry.RecordLlmCallStarted(ResponseApiProvider, $"{_deploymentId}:{phaseName}");
            llmStopwatch = Stopwatch.StartNew();

            _logger.LogInformation("Requesting {PhaseName} reasoning step from the Azure OpenAI Response API.", phaseName);
            _logger.LogInformation(
                "Response API {PhaseName} request details. System prompt: {SystemPrompt}; Prompt payload: {PromptPayload}",
                phaseName,
                inputItems.OfType<MessageResponseItem>().FirstOrDefault(m => m.Role == MessageRole.System)?.Content.FirstOrDefault()?.Text ?? string.Empty,
                dumpAllPrompt);
            OpenAIResponse response = await responseClient.CreateResponseAsync(inputItems, options).ConfigureAwait(false);
            llmStopwatch.Stop();
            SgrTelemetry.RecordLlmCallCompleted(modelActivity, llmStopwatch.Elapsed);

            await DumpLlmCallAsync(_llmCallCounter, phaseName, dumpAllPrompt, schema, response).ConfigureAwait(false);

            if (response.Usage != null)
            {
                var usage = new TokenUsage
                {
                    InputTokenCount = response.Usage.InputTokenCount,
                    OutputTokenCount = response.Usage.OutputTokenCount,
                    TotalTokenCount = response.Usage.TotalTokenCount
                };

                CurrentSessionStats.AddUsage(usage);
                SgrTelemetry.RecordUsage(
                    modelActivity,
                    usage.InputTokenCount,
                    usage.OutputTokenCount,
                    usage.TotalTokenCount);

                if (VerboseOutput)
                {
                    DisplayTokenUsage(usage, _llmCallCounter, phaseName);
                }
            }

            var responseMessage = response.OutputItems.OfType<MessageResponseItem>().FirstOrDefault();
            if (responseMessage == null)
            {
                throw new InvalidOperationException("OpenAI Response did not contain a message item.");
            }

            var assistantRaw = string.Join("\n", responseMessage.Content.Select(c => c.Text));
            SgrTelemetry.RecordModelResponse(modelActivity, assistantRaw);
            _logger.LogInformation("Response API {PhaseName} assistant response: {AssistantResponse}", phaseName, assistantRaw);

            if (string.IsNullOrEmpty(assistantRaw))
            {
                throw new InvalidOperationException("OpenAI API did not return a message");
            }

            var nextStep = planningSchemaManager.DeserializeFromJson(assistantRaw);
            if (nextStep == null)
            {
                throw new InvalidOperationException("Failed to deserialize ForcedReasonerNextStep from OpenAI response:\n" + assistantRaw);
            }

            if (nextStep.Function == null)
            {
                throw new InvalidOperationException("LLM did not provide a tool call in the structured planning response.");
            }

            if (VerboseOutput)
            {
                AnsiConsole.MarkupLine($"[grey]{Markup.Escape(phaseName)} raw response:[/]");
                AnsiConsole.WriteLine(Markup.Escape(assistantRaw));
            }

            return nextStep;
        }
        catch (Exception)
        {
            if (llmStopwatch?.IsRunning == true)
            {
                llmStopwatch.Stop();
                SgrTelemetry.RecordLlmCallFailed(modelActivity, llmStopwatch.Elapsed);
            }

            throw;
        }
        finally
        {
            modelActivity?.Dispose();
        }
    }

    private async Task<ToolCall> RequestToolParametersAsync(
        OpenAIResponseClient responseClient,
        List<ResponseItem> inputItems,
        string schema,
        string phaseName,
        Type toolCallType)
    {
        Stopwatch? llmStopwatch = null;
        Activity? modelActivity = null;

        try
        {
            var dumpAllPrompt = Dump(inputItems);

            _llmCallCounter++;

            var options = new ResponseCreationOptions
            {
                PreviousResponseId = null,
                ReasoningOptions = new ResponseReasoningOptions()
                {
                    ReasoningEffortLevel = ReasoningEffortLevel
                },
                TextOptions = new ResponseTextOptions
                {
                    TextFormat = ResponseTextFormat.CreateJsonSchemaFormat(
                        jsonSchemaFormatName: toolCallType.Name,
                        jsonSchema: BinaryData.FromString(schema),
                        jsonSchemaFormatDescription: $"Schema for {toolCallType.Name} parameters",
                        jsonSchemaIsStrict: true)
                },
            };

            var systemPrompt = inputItems.OfType<MessageResponseItem>()
                .FirstOrDefault(m => m.Role == MessageRole.System)?
                .Content.FirstOrDefault()?.Text ?? string.Empty;

            modelActivity = SgrTelemetry.StartModelCall(
                provider: ResponseApiProvider,
                model: $"{_deploymentId}:{phaseName}:{toolCallType.Name}",
                systemPrompt: systemPrompt,
                userPrompt: dumpAllPrompt,
                schema: schema);
            SgrTelemetry.RecordLlmCallStarted(ResponseApiProvider, $"{_deploymentId}:{phaseName}:{toolCallType.Name}");
            llmStopwatch = Stopwatch.StartNew();

            _logger.LogInformation("Requesting {PhaseName} tool parameters for {ToolName} from the Azure OpenAI Response API.", phaseName, toolCallType.Name);
            _logger.LogInformation(
                "Response API {PhaseName} request details. System prompt: {SystemPrompt}; Prompt payload: {PromptPayload}",
                phaseName,
                systemPrompt,
                dumpAllPrompt);

            OpenAIResponse response = await responseClient.CreateResponseAsync(inputItems, options).ConfigureAwait(false);
            llmStopwatch.Stop();
            SgrTelemetry.RecordLlmCallCompleted(modelActivity, llmStopwatch.Elapsed);

            await DumpLlmCallAsync(_llmCallCounter, phaseName, dumpAllPrompt, schema, response).ConfigureAwait(false);

            if (response.Usage != null)
            {
                var usage = new TokenUsage
                {
                    InputTokenCount = response.Usage.InputTokenCount,
                    OutputTokenCount = response.Usage.OutputTokenCount,
                    TotalTokenCount = response.Usage.TotalTokenCount
                };

                CurrentSessionStats.AddUsage(usage);
                SgrTelemetry.RecordUsage(modelActivity, usage.InputTokenCount, usage.OutputTokenCount, usage.TotalTokenCount);

                if (VerboseOutput)
                {
                    DisplayTokenUsage(usage, _llmCallCounter, phaseName);
                }
            }

            var responseMessage = response.OutputItems.OfType<MessageResponseItem>().FirstOrDefault();
            if (responseMessage == null)
            {
                throw new InvalidOperationException("OpenAI Response did not contain a message item.");
            }

            var assistantRaw = string.Join("\n", responseMessage.Content.Select(c => c.Text));
            SgrTelemetry.RecordModelResponse(modelActivity, assistantRaw);
            _logger.LogInformation("Response API {PhaseName} assistant response: {AssistantResponse}", phaseName, assistantRaw);

            if (string.IsNullOrWhiteSpace(assistantRaw))
            {
                throw new InvalidOperationException("OpenAI API did not return a tool parameter payload.");
            }

            var toolCall = BusinessFunctionFactory.DeserializeToolCall(assistantRaw, toolCallType);
            if (toolCall == null)
            {
                throw new InvalidOperationException($"Failed to deserialize {toolCallType.Name} from OpenAI response:\n{assistantRaw}");
            }

            if (VerboseOutput)
            {
                AnsiConsole.MarkupLine($"[grey]{Markup.Escape(phaseName)} raw response:[/]");
                AnsiConsole.WriteLine(Markup.Escape(assistantRaw));
            }

            return toolCall;
        }
        catch (Exception)
        {
            if (llmStopwatch?.IsRunning == true)
            {
                llmStopwatch.Stop();
                SgrTelemetry.RecordLlmCallFailed(modelActivity, llmStopwatch.Elapsed);
            }

            throw;
        }
        finally
        {
            modelActivity?.Dispose();
        }
    }

    private static void DisplayPlannedStep(ForcedReasonerNextStep nextStep)
    {
        if (nextStep.PlanRemainingStepsBrief != null && nextStep.PlanRemainingStepsBrief.Count > 0)
        {
            AnsiConsole.MarkupLine("[cyan]  Planned remaining steps:[/]");
            for (int i = 0; i < nextStep.PlanRemainingStepsBrief.Count; i++)
            {
                var step = nextStep.PlanRemainingStepsBrief[i];
                var description = step?.Description ?? string.Empty;
                var toolId = step?.ToolId ?? string.Empty;
                AnsiConsole.MarkupLine($"[cyan]    {i + 1}. {Markup.Escape(description)}[/] [grey]({Markup.Escape(toolId)})[/]");
            }
        }
        else
        {
            AnsiConsole.MarkupLine("[cyan]  Planned remaining steps:[/] [dim]None[/]");
        }

        var currentPlan = nextStep.PlanRemainingStepsBrief?.FirstOrDefault()?.Description ?? "No plan specified";
        var toolName = nextStep.Function?.GetType().Name.Replace("ToolCall", string.Empty) ?? "unknown";
        AnsiConsole.MarkupLine($"[cyan]  First Pass Tool Guess:[/] [yellow]{Markup.Escape(toolName)}[/] - {Markup.Escape(currentPlan)}");
    }

    private static void DisplayForcedToolSelection(ForcedReasonerPlanStep firstPlannedStep, ToolCall selectedTool)
    {
        var toolName = selectedTool.GetType().Name.Replace("ToolCall", string.Empty);
        AnsiConsole.MarkupLine($"[cyan]  Forced Step:[/] [yellow]{Markup.Escape(toolName)}[/] - {Markup.Escape(firstPlannedStep.Description)} [grey]({Markup.Escape(firstPlannedStep.ToolId)})[/]");

        try
        {
            var nextStepJson = JsonConvert.SerializeObject(selectedTool, Formatting.Indented);
            AnsiConsole.MarkupLine("[dim]  Tool parameters:[/]");
            AnsiConsole.WriteLine(Markup.Escape(nextStepJson));
        }
        catch (Exception)
        {
            AnsiConsole.MarkupLine($"[dim]    (Unable to serialize tool parameters for {Markup.Escape(toolName)})[/]");
        }
    }

    private static string Dump(List<ResponseItem> inputItems)
    {
        StringBuilder sb = new();
        foreach (var item in inputItems)
        {
            if (item is MessageResponseItem mri)
            {
                sb.AppendLine($"[{mri.Role}] {string.Join("", mri.Content.Select(c => c.Text))}");
            }
            else if (item is FunctionCallResponseItem fcri)
            {
                sb.AppendLine($"[FunctionCall: {fcri.FunctionName}] {fcri.FunctionArguments}");
            }
            else if (item is FunctionCallOutputResponseItem fco)
            {
                sb.AppendLine($"[FunctionCallOutput: {fco.CallId}] {fco.FunctionOutput}");
            }
        }

        return sb.ToString();
    }

    private static void DisplayTokenUsage(TokenUsage usage, int callNumber, string phaseName)
    {
        AnsiConsole.MarkupLine($"[grey]  Call {callNumber} ({Markup.Escape(phaseName)}) Tokens:[/]");
        AnsiConsole.MarkupLine($"[grey]    Input: {usage.InputTokenCount}[/]");
        AnsiConsole.MarkupLine($"[grey]    Output: {usage.OutputTokenCount}[/]");
        AnsiConsole.MarkupLine($"[grey]    Total: {usage.TotalTokenCount}[/]");

        if (usage.InputTokenDetails?.CachedTokenCount > 0)
        {
            AnsiConsole.MarkupLine($"[green]    └─ Cached Input: {usage.InputTokenDetails.CachedTokenCount}[/]");
        }

        if (usage.OutputTokenDetails?.ReasoningTokenCount > 0)
        {
            AnsiConsole.MarkupLine($"[fuchsia]    └─ Reasoning: {usage.OutputTokenDetails.ReasoningTokenCount}[/]");
        }
    }

    private void EnsureLlmCallsDirectory()
    {
        if (!Directory.Exists(_llmCallsDirectory))
        {
            Directory.CreateDirectory(_llmCallsDirectory);
        }
    }

    private async Task DumpLlmCallAsync(int callNumber, string phaseName, string prompt, string schema, OpenAIResponse response)
    {
        try
        {
            var prefix = $"{callNumber:D3}-{phaseName}";

            var promptFile = Path.Combine(_llmCallsDirectory, $"{prefix}-prompt.txt");
            await File.WriteAllTextAsync(promptFile, prompt).ConfigureAwait(false);

            var schemaFile = Path.Combine(_llmCallsDirectory, $"{prefix}-schema.json");
            await File.WriteAllTextAsync(schemaFile, schema).ConfigureAwait(false);

            var responseFile = Path.Combine(_llmCallsDirectory, $"{prefix}-response.json");
            var responseMessage = response.OutputItems.OfType<MessageResponseItem>().FirstOrDefault();
            if (responseMessage != null)
            {
                var responseJson = string.Join("\n", responseMessage.Content.Select(c => c.Text));
                await File.WriteAllTextAsync(responseFile, responseJson).ConfigureAwait(false);
            }
            else
            {
                var fullResponse = JsonConvert.SerializeObject(response, Formatting.Indented);
                await File.WriteAllTextAsync(responseFile, fullResponse).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            if (VerboseOutput)
            {
                AnsiConsole.MarkupLine($"[yellow]Warning: Failed to dump LLM call {callNumber}: {Markup.Escape(ex.Message)}[/]");
            }
        }
    }

    private string BuildForcedToolPrompt(string baseSystemPrompt, ForcedReasonerPlanStep firstPlannedStep, Type toolCallType)
    {
        var discriminator = BusinessFunctionFactory.GetToolDiscriminatorValue(toolCallType);
        return $@"{baseSystemPrompt}

## Forced Tool Refinement
You already planned the next action.
Focus only on the first planned step below and produce the parameters for the matching tool.

First planned step:
- description: {firstPlannedStep.Description}
- toolid: {firstPlannedStep.ToolId}

You must answer ONLY with a JSON object matching the {toolCallType.Name} schema.
Do not return the NextStep wrapper.
Do not return explanations.
The tool discriminator/type must be ""{discriminator}"".";
    }

    private PolymorphicSchemaManager<ForcedReasonerNextStep, ToolCall> CreateForcedPlanningSchemaManager(IEnumerable<Type>? availableToolTypes)
    {
        var toolTypes = (availableToolTypes ?? _functionFactory.SchemaManager.DerivedPolymorphicTypes).ToArray();
        return new PolymorphicSchemaManager<ForcedReasonerNextStep, ToolCall>("type").AddDerivedTypes(toolTypes);
    }

    private Type IdentifyToolTypeFromPlan(
        ForcedReasonerPlanStep firstPlannedStep,
        IEnumerable<Type>? availableToolTypes,
        ForcedReasonerNextStep plannedStep)
    {
        var candidateTypes = (availableToolTypes ?? _functionFactory.SchemaManager.DerivedPolymorphicTypes).ToList();
        var normalizedToolId = Normalize(firstPlannedStep.ToolId);

        var directMatch = candidateTypes.FirstOrDefault(type =>
            string.Equals(
                Normalize(BusinessFunctionFactory.GetToolDiscriminatorValue(type)),
                normalizedToolId,
                StringComparison.Ordinal));

        if (directMatch != null)
        {
            _logger.LogInformation(
                "Identified forced tool {ToolType} from first planned step tool id {ToolId}.",
                directMatch.Name,
                firstPlannedStep.ToolId);
            return directMatch;
        }

        if (plannedStep.Function != null)
        {
            _logger.LogWarning(
                "Could not identify tool from first planned step tool id '{ToolId}'. Falling back to first-pass tool {ToolType}.",
                firstPlannedStep.ToolId,
                plannedStep.Function.GetType().Name);
            return plannedStep.Function.GetType();
        }

        throw new InvalidOperationException($"Could not identify a tool to execute from the first planned step tool id '{firstPlannedStep.ToolId}'.");
    }

    private bool TryCompleteFromPlannedStep(ForcedReasonerNextStep plannedStep, out string completionSummary)
    {
        if (!plannedStep.TaskCompleted)
        {
            completionSummary = string.Empty;
            return false;
        }

        completionSummary = (plannedStep.Function as ReportTaskCompletionToolCall)?.Summary
            ?? plannedStep.CurrentState
            ?? "Task completed";
        return true;
    }

    private async Task<ToolCall> ResolveSelectedToolAsync(
        OpenAIResponseClient responseClient,
        string userRequest,
        List<ToolExecutionResult> executionTaskResult,
        string systemPrompt,
        IEnumerable<Type>? availableToolTypes,
        ForcedReasonerNextStep plannedStep)
    {
        var firstPlannedStep = plannedStep.PlanRemainingStepsBrief?.FirstOrDefault()
            ?? throw new InvalidOperationException("The planning step did not provide a first remaining step.");
        var forcedToolType = IdentifyToolTypeFromPlan(firstPlannedStep, availableToolTypes, plannedStep);
        var forcedSchema = _functionFactory.GenerateJsonSchemaForToolParameters(forcedToolType);
        var forcedPrompt = BuildForcedToolPrompt(systemPrompt, firstPlannedStep, forcedToolType);
        var forcedInputItems = BuildInputItems(userRequest, executionTaskResult, forcedPrompt);

        var selectedTool = await RequestToolParametersAsync(
            responseClient,
            forcedInputItems,
            forcedSchema,
            "forced",
            forcedToolType).ConfigureAwait(false);

        DisplayForcedToolSelection(firstPlannedStep, selectedTool);
        return selectedTool;
    }

    private async Task ExecuteSelectedToolAsync(ToolCall selectedTool, List<ToolExecutionResult> executionTaskResult)
    {
        var toolName = selectedTool.GetType().Name.Replace("ToolCall", string.Empty);
        var businessResult = await _functionFactory.DispatchToolFunction(selectedTool).ConfigureAwait(false);
        executionTaskResult.Add(new ToolExecutionResult(
            toolName,
            JsonConvert.SerializeObject(selectedTool),
            businessResult.Summary));

        if (VerboseOutput)
        {
            AnsiConsole.Write("[green]    ✓ [/]");
            AnsiConsole.WriteLine(Markup.Escape(businessResult.Summary));
            return;
        }

        AnsiConsole.MarkupLine($"  [grey]→[/] {Markup.Escape(businessResult.Summary)}");
    }

    private string CompleteReasoning(string completionContext, string completionSummary)
    {
        var completionSuffix = string.IsNullOrWhiteSpace(completionContext)
            ? string.Empty
            : $" {completionContext}";
        _logger.LogInformation(
            "Forced-tool Response API SGR execution completed{CompletionContext}: {Summary}",
            completionSuffix,
            completionSummary);

        if (VerboseOutput)
        {
            AnsiConsole.MarkupLine($"[blue]Task completed: {Markup.Escape(completionSummary)}[/]");
            DisplaySessionTokenUsageSummary();
        }

        return completionSummary;
    }

    private string ReportReasoningFailure(int step, Exception ex)
    {
        AnsiConsole.Write("[red]Error in reasoning step ");
        AnsiConsole.Write(step.ToString());
        AnsiConsole.Write(": [/]");
        AnsiConsole.WriteLine(ex.Message);

        if (VerboseOutput)
        {
            AnsiConsole.MarkupLine("\n[bold cyan]Session Token Usage Summary (up to error):[/]");
            AnsiConsole.WriteLine(CurrentSessionStats.ToString());
        }

        return $"Error occurred during reasoning: {ex.Message}";
    }

    private void DisplaySessionTokenUsageSummary()
    {
        if (!VerboseOutput)
        {
            return;
        }

        AnsiConsole.MarkupLine("\n[bold cyan]Session Token Usage Summary:[/]");
        AnsiConsole.WriteLine(CurrentSessionStats.ToString());
    }

    private static string Normalize(string value) =>
        value.Trim().Replace("-", " ").Replace("_", " ").ToLowerInvariant();
}
