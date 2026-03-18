using Azure;
using Azure.AI.OpenAI;
using Newtonsoft.Json;
using OpenAI.Responses;
using Alkampfer.Sgr.Models;
using Alkampfer.Sgr.BusinessFunctions;
using Alkampfer.Sgr.Telemetry;
using Spectre.Console;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using System.Text;

namespace Alkampfer.Sgr.Runtime;

#pragma warning disable OPENAI001

/// <summary>
/// Schema-guided reasoner that uses a two-phase strategy for each step:
/// first it plans with the full discriminated union, then it re-issues the
/// same prompt constrained to the selected tool type to obtain final arguments.
/// </summary>
public class ResponseApiForcedToolSchemaGuidedReasoner
{
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
    private string _llmCallsDirectory = Path.Combine(Directory.GetCurrentDirectory(), "llm_calls");

    private record ToolExecutionResult(string ToolName, string Parameters, string Summary);

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
        using var executionActivity = SgrTelemetry.StartReasonerExecution("response-api-forced-tool", userRequest);

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
            using var stepActivity = SgrTelemetry.StartPlanningStep("response-api-forced-tool", step);
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

                var planningSchema = availableToolTypes != null
                    ? _functionFactory.GenerateJsonSchemaForToolCall(availableToolTypes)
                    : _functionFactory.GenerateJsonSchemaForToolCall();

                var plannedStep = await RequestNextStepAsync(
                    responseClient,
                    planningInputItems,
                    planningSchema,
                    "plan").ConfigureAwait(false);

                if (VerboseOutput)
                {
                    AnsiConsole.MarkupLine($"[grey]First pass selected tool:[/] {Markup.Escape(plannedStep.Function.GetType().Name)}");
                }

                var forcedToolType = plannedStep.Function.GetType();
                var forcedSchema = _functionFactory.GenerateJsonSchemaForToolCall([forcedToolType]);
                var forcedStep = await RequestNextStepAsync(
                    responseClient,
                    planningInputItems,
                    forcedSchema,
                    "forced").ConfigureAwait(false);

                DisplayNextStep(forcedStep);

                if (forcedStep.Function is ReportTaskCompletionToolCall completionParameter)
                {
                    _logger.LogInformation("Forced-tool Response API SGR execution completed: {Summary}", completionParameter.Summary);
                    if (VerboseOutput)
                    {
                        AnsiConsole.MarkupLine($"[blue]Task completed: {Markup.Escape(completionParameter.Summary)}[/]");
                        AnsiConsole.MarkupLine("\n[bold cyan]Session Token Usage Summary:[/]");
                        AnsiConsole.WriteLine(CurrentSessionStats.ToString());
                    }

                    return completionParameter.Summary;
                }

                var selectedTool = forcedStep.Function
                    ?? throw new InvalidOperationException("Forced tool refinement did not return a tool call.");
                var toolName = selectedTool.GetType().Name.Replace("ToolCall", "") ?? "unknown";
                var businessResult = await _functionFactory.DispatchToolFunction(selectedTool).ConfigureAwait(false);
                executionTaskResult.Add(new ToolExecutionResult(
                    toolName,
                    JsonConvert.SerializeObject(selectedTool),
                    businessResult.Summary));

                if (VerboseOutput)
                {
                    AnsiConsole.Write("[green]    ✓ [/]");
                    AnsiConsole.WriteLine(Markup.Escape(businessResult.Summary));
                }
                else
                {
                    AnsiConsole.MarkupLine($"  [grey]→[/] {Markup.Escape(businessResult.Summary)}");
                }
            }
            catch (Exception ex)
            {
                SgrTelemetry.MarkError(stepActivity, ex);
                SgrTelemetry.MarkError(executionActivity, ex);
                _logger.LogError(ex, "Forced-tool Response API reasoning failed at step {Step}", step);
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
        }

        if (VerboseOutput)
        {
            AnsiConsole.MarkupLine("\n[bold cyan]Session Token Usage Summary:[/]");
            AnsiConsole.WriteLine(CurrentSessionStats.ToString());
        }

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

    private List<ResponseItem> BuildInputItems(
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

    private async Task<NextStep> RequestNextStepAsync(
        OpenAIResponseClient responseClient,
        List<ResponseItem> inputItems,
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
                        jsonSchemaFormatName: "NextStep",
                        jsonSchema: BinaryData.FromString(schema),
                        jsonSchemaFormatDescription: "Schema for NextStep with polymorphic ToolCall support",
                        jsonSchemaIsStrict: true)
                },
            };

            modelActivity = SgrTelemetry.StartModelCall(
                provider: "azure-openai-response-api",
                model: $"{_deploymentId}:{phaseName}",
                systemPrompt: inputItems.OfType<MessageResponseItem>().FirstOrDefault(m => m.Role == MessageRole.System)?.Content.FirstOrDefault()?.Text ?? string.Empty,
                userPrompt: dumpAllPrompt,
                schema: schema);
            SgrTelemetry.RecordLlmCallStarted("azure-openai-response-api", $"{_deploymentId}:{phaseName}");
            llmStopwatch = Stopwatch.StartNew();

            _logger.LogInformation("Requesting {PhaseName} reasoning step from the Azure OpenAI Response API.", phaseName);
            _logger.LogInformation("Response API {PhaseName} system prompt: {SystemPrompt}",
                phaseName,
                inputItems.OfType<MessageResponseItem>().FirstOrDefault(m => m.Role == MessageRole.System)?.Content.FirstOrDefault()?.Text ?? string.Empty);
            _logger.LogInformation("Response API {PhaseName} prompt payload: {PromptPayload}", phaseName, dumpAllPrompt);
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

            var nextStep = _functionFactory.DeserializeNextStep(assistantRaw);
            if (nextStep == null)
            {
                throw new InvalidOperationException("Failed to deserialize NextStep from OpenAI response:\n" + assistantRaw);
            }

            if (nextStep.Function == null)
            {
                throw new InvalidOperationException("LLM did not provide a NextStepToolToCall in the NextStep response.");
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

    private void DisplayNextStep(NextStep nextStep)
    {
        if (nextStep.PlanRemainingStepsBrief != null && nextStep.PlanRemainingStepsBrief.Count > 0)
        {
            AnsiConsole.MarkupLine("[cyan]  Planned remaining steps:[/]");
            for (int i = 0; i < nextStep.PlanRemainingStepsBrief.Count; i++)
            {
                var stepText = nextStep.PlanRemainingStepsBrief[i] ?? string.Empty;
                AnsiConsole.MarkupLine($"[cyan]    {i + 1}. {Markup.Escape(stepText)}[/]");
            }
        }
        else
        {
            AnsiConsole.MarkupLine("[cyan]  Planned remaining steps:[/] [dim]None[/]");
        }

        var currentPlan = nextStep.PlanRemainingStepsBrief?.FirstOrDefault() ?? "No plan specified";
        var toolName = nextStep.Function?.GetType().Name.Replace("ToolCall", "") ?? "unknown";
        AnsiConsole.MarkupLine($"[cyan]  Next Step:[/] [yellow]{Markup.Escape(toolName)}[/] - {Markup.Escape(currentPlan)}");

        try
        {
            var nextStepJson = JsonConvert.SerializeObject(nextStep.Function, Formatting.Indented);
            AnsiConsole.MarkupLine("[dim]  Tool parameters:[/]");
            AnsiConsole.WriteLine(Markup.Escape(nextStepJson));
        }
        catch (Exception)
        {
            AnsiConsole.MarkupLine($"[dim]    (Unable to serialize NextStep for {Markup.Escape(toolName)})[/]");
        }
    }

    private string Dump(List<ResponseItem> inputItems)
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

    private void DisplayTokenUsage(TokenUsage usage, int callNumber, string phaseName)
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
}
