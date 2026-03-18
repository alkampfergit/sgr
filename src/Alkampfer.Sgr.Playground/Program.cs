using Microsoft.Extensions.Logging;
using Alkampfer.Sgr.Services;
using Alkampfer.Sgr.Telemetry;
using Alkampfer.Sgr.Utils;
using Alkampfer.Sgr.Runtime;
using Spectre.Console;
using Alkampfer.Sgr.BusinessFunctions;
using Alkampfer.Sgr.Playground;
using OpenAI.Responses;
using System.Diagnostics;

/// <summary>
/// Main program class for the Schema-Guided Reasoning playground
/// Demonstrates various AI reasoning scenarios using the direct Azure OpenAI Response API
/// </summary>
class Program
{
    private static ResponseApiSchemaGuidedReasoner? responseApiReasoner;
    private static ResponseApiForcedToolSchemaGuidedReasoner? forcedResponseApiReasoner;
    private static PlaygroundTelemetry? telemetry;
    private static ILogger<Program>? logger;
    private static PlaygroundReasonerMode selectedReasonerMode = PlaygroundReasonerMode.Normal;

    private enum PlaygroundReasonerMode
    {
        Normal,
        Forced
    }

    static async Task Main(string[] args)
    {
        using var playgroundTelemetry = PlaygroundTelemetry.Create();
        telemetry = playgroundTelemetry;
        logger = playgroundTelemetry.LoggerFactory.CreateLogger<Program>();

        System.Data.Common.DbProviderFactories.RegisterFactory(
            "Microsoft.Data.SqlClient",
            Microsoft.Data.SqlClient.SqlClientFactory.Instance);
        // DataAccess.SetConnectionString(
        //     "Server=localhost\\SQLEXPRESS;Database=master;Trusted_Connection=True;TrustServerCertificate=True;",
        //     "Microsoft.Data.SqlClient",
        //    NullLogger.Instance);
        // Display application header with styling
        AnsiConsole.Write(
            new FigletText("SGR Playground")
                .Centered()
                .Color(Color.Blue));

        AnsiConsole.Write(
            new Rule("[bold blue]Schema-Guided Reasoning with C# and Direct Azure OpenAI[/]")
                .RuleStyle("grey"));

        AnsiConsole.MarkupLine($"[grey]OpenTelemetry OTLP endpoint:[/] {playgroundTelemetry.OtlpEndpoint}");
        logger.LogInformation("Playground telemetry initialized with OTLP endpoint {OtlpEndpoint}", playgroundTelemetry.OtlpEndpoint);

        // Ask user about verbose output preference
        var verboseOutput = AnsiConsole.Confirm(
            "[yellow]Enable verbose output?[/] [grey](Shows detailed JSON responses and debug information)[/]",
            defaultValue: false);

        AnsiConsole.WriteLine();

        selectedReasonerMode = PromptReasonerMode();

        // Initialize the reasoner
        await InitializeReasoner(verboseOutput);

        // Main application loop
        while (true)
        {
            var selectedExample = ShowExampleMenu();

            if (selectedExample == "exit")
                break;

            await ExecuteExample(selectedExample);

            // Wait for user to press a key before continuing
            AnsiConsole.Write(new Rule("[dim]Press any key to continue...[/]").RuleStyle("grey"));
            Console.ReadKey(true);
            AnsiConsole.Clear();
        }

        AnsiConsole.Write(
            new Panel("[green]Thank you for using the Schema-Guided Reasoning Playground![/]")
                .Border(BoxBorder.Rounded)
                .Padding(1, 0));
    }

    /// <summary>
    /// Initialize the direct Response API reasoner and supporting business functions.
    /// </summary>
    /// <param name="verboseOutput">Whether to enable verbose output in the reasoner</param>
    private static async Task InitializeReasoner(bool verboseOutput)
    {
        var apiKey = GetRequiredSetting("OPENAI_API_KEY");
        var endpoint = GetRequiredSetting("AZURE_ENDPOINT");
        var deploymentId = "gpt-5-mini";

        var openAiConfiguration = new AzureOpenAiConfiguration(endpoint, apiKey, deploymentId);
        AnsiConsole.Status()
            .Start("[yellow]Initializing Direct OpenAI API Reasoner...[/]", ctx =>
            {
                var databaseService = new DatabaseService();
                var businessFunctionFactory = SchemaGuidedReasonerFactory.CreateDefaultBusinessFunctionFactory(
                    databaseService,
                    openAiConfiguration,
                    telemetry!.LoggerFactory);
                var options = SchemaGuidedReasonerFactory.CreateDefaultOptions(databaseService);

                try
                {
                    responseApiReasoner = null;
                    forcedResponseApiReasoner = null;

                    if (selectedReasonerMode == PlaygroundReasonerMode.Forced)
                    {
                        forcedResponseApiReasoner = new ResponseApiForcedToolSchemaGuidedReasoner(
                            azureEndpoint: endpoint,
                            azureApiKey: apiKey,
                            deploymentId: deploymentId,
                            businessFunctionFactory: businessFunctionFactory,
                            options: options,
                            logger: telemetry!.LoggerFactory.CreateLogger<ResponseApiForcedToolSchemaGuidedReasoner>())
                        {
                            VerboseOutput = verboseOutput,
#pragma warning disable OPENAI001
                            ReasoningEffortLevel = ResponseReasoningEffortLevel.Low
#pragma warning restore OPENAI001
                        };
                    }
                    else
                    {
                        responseApiReasoner = new ResponseApiSchemaGuidedReasoner(
                            azureEndpoint: endpoint,
                            azureApiKey: apiKey,
                            deploymentId: deploymentId,
                            businessFunctionFactory: businessFunctionFactory,
                            options: options,
                            logger: telemetry!.LoggerFactory.CreateLogger<ResponseApiSchemaGuidedReasoner>())
                        {
                            VerboseOutput = verboseOutput,
#pragma warning disable OPENAI001
                            ReasoningEffortLevel = ResponseReasoningEffortLevel.Low
#pragma warning restore OPENAI001
                        };
                    }

                    ctx.Status($"[green]{GetSelectedReasonerLabel()} ready![/]");
                }
                catch (Exception ex)
                {
                    ctx.Status($"[red]Error: {ex.Message}[/]");
                    throw;
                }
            });

        var outputMode = verboseOutput ? "verbose" : "concise";
        AnsiConsole.MarkupLine($"[green]✓[/] {GetSelectedReasonerLabel()} initialized successfully! [grey]({outputMode} output)[/]");
        AnsiConsole.WriteLine();

        await Task.CompletedTask;
    }

    private static PlaygroundReasonerMode PromptReasonerMode()
    {
        var selection = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[yellow]Choose the reasoning strategy:[/]")
                .PageSize(5)
                .AddChoices([
                    "Normal reasoner",
                    "Forced reasoner"
                ]));

        return selection switch
        {
            "Forced reasoner" => PlaygroundReasonerMode.Forced,
            _ => PlaygroundReasonerMode.Normal
        };
    }

    /// <summary>
    /// Display the interactive menu for selecting examples
    /// Uses Spectre.Console for vibrant UI
    /// </summary>
    private static string ShowExampleMenu()
    {
        var selection = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold blue]Select an example to run:[/]")
                .PageSize(10)
                .MoreChoicesText("[grey](Move up and down to reveal more examples)[/]")
                .AddChoices([
                    "Original Python Tasks (SGR Demo)",
                    "Simple Email Task",
                    "Customer Support Workflow",
                    "Search table in database",
                    "SQL Database + Excel Export",
                    "Exit"
                ]));

        return selection.ToLowerInvariant().Replace(" ", "_");
    }

    /// <summary>
    /// Execute the selected example based on user choice
    /// </summary>
    private static async Task ExecuteExample(string exampleType)
    {
        switch (exampleType)
        {
            case "original_python_tasks_(sgr_demo)":
                await RunOriginalPythonTasksExample();
                break;
            case "simple_email_task":
                await RunSimpleEmailExample();
                break;
            case "customer_support_workflow":
                await RunCustomerSupportWorkflowExample();
                break;
            case "sql_database_+_excel_export":
                await RunSqlExample("Please give me a summary of all the orders grouped by year and customer code in the database northwind exported in excel");
                break;
            case "search_table_in_database":
                await RunSqlExample("Is there a table order in the northwind database?");
                break;
            case "exit":
                return;
            default:
                AnsiConsole.MarkupLine("[red]Invalid selection![/]");
                break;
        }
    }

    /// <summary>
    /// Execute a task using the direct Response API reasoner.
    /// </summary>
    private static async Task<string> ExecuteReasoningTask(string task)
    {
        return selectedReasonerMode switch
        {
            PlaygroundReasonerMode.Forced => await forcedResponseApiReasoner!.ReasonAndActAsync(task),
            _ => await responseApiReasoner!.ReasonAndActAsync(task)
        };
    }

    /// <summary>
    /// Example 0: Run the original Python tasks to demonstrate Schema-Guided Reasoning
    /// This matches the TASKS array from the Python original for direct comparison
    /// </summary>
    private static async Task RunOriginalPythonTasksExample()
    {
        var tasks = new[]
        {
            "Rule: address sama@openai.com as 'The SAMA', always give him 5% discount.",
            "Rule for elon@x.com: Email his invoices to finance@x.com",
            "sama@openai.com wants one of each product. Email him the invoice",
            "elon@x.com wants 2x of what sama@openai.com got. Send invoice",
            "redo last elon@x.com invoice: use 3x discount of sama@openai.com"
        };

        await RunScenarioAsync("Original Python Tasks", string.Join(Environment.NewLine, tasks), async () =>
        {
            AnsiConsole.Write(
                new Panel("[bold red]🚀 Original Python Tasks - Schema-Guided Reasoning Demo[/]\n[dim]Using: Direct OpenAI API[/]")
                    .Border(BoxBorder.Rounded)
                    .BorderColor(Color.Red));

            foreach (var (task, index) in tasks.Select((t, i) => (t, i + 1)))
            {
                using var taskActivity = SgrTelemetry.ActivitySource.StartActivity("sgr.example.task", ActivityKind.Internal);
                taskActivity?.SetTag("sgr.example.task.index", index);
                taskActivity?.SetTag("sgr.user.request", task);

                AnsiConsole.WriteLine();
                AnsiConsole.Write(
                    new Rule($"[bold blue]Task {index}[/]")
                        .RuleStyle("blue"));

                AnsiConsole.MarkupLine($"[dim]Task:[/] {task}");
                AnsiConsole.WriteLine();

                try
                {
                    var result = await AnsiConsole.Status()
                        .StartAsync($"[yellow]Executing task {index} with SGR...[/]", async ctx =>
                        {
                            return await ExecuteReasoningTask(task);
                        });

                    AnsiConsole.Write(
                        new Panel($"[green]Task {index} Result:[/] {Markup.Escape(result)}")
                            .Header($"Task {index} Complete")
                            .Border(BoxBorder.Rounded)
                            .BorderColor(Color.Green));
                }
                catch (Exception ex)
                {
                    SgrTelemetry.MarkError(taskActivity, ex);
                    AnsiConsole.Write(
                        new Panel($"[red]Error in Task {index}:[/] {ex.Message}")
                            .Header($"Task {index} Failed")
                            .Border(BoxBorder.Rounded)
                            .BorderColor(Color.Red));
                }
            }

            AnsiConsole.WriteLine();
            AnsiConsole.Write(
                new Panel("""
                [bold green]Schema-Guided Reasoning Demonstration Complete![/]

                This demo shows how the C# implementation now matches the Python original:
                • [yellow]Structured Reasoning:[/] LLM generates NextStep JSON on each turn
                • [yellow]Manual Tool Dispatch:[/] No automatic tool calling - explicit control
                • [yellow]Step-by-step Execution:[/] Clear reasoning progression
                • [yellow]Task-oriented:[/] Multi-step business logic handled correctly
                """)
                    .Header("SGR Demo Results")
                    .Border(BoxBorder.Rounded)
                    .BorderColor(Color.Green));
        });
    }

    /// <summary>
    /// Example 1: Demonstrate simple email sending task
    /// Shows basic schema-guided reasoning for a straightforward operation
    /// </summary>
    private static async Task RunSimpleEmailExample()
    {
        var prompt = "Send an email to john@example.com with subject 'Welcome' and body 'Thank you for joining us!'";
        await RunScenarioAsync("Simple Email Task", prompt, async () =>
        {
            AnsiConsole.Write(
                new Panel("[bold yellow]🧪 Test 1: Simple Email Task[/]\n[dim]Using: Direct OpenAI API[/]")
                    .Border(BoxBorder.Rounded)
                    .BorderColor(Color.Yellow));

            AnsiConsole.MarkupLine($"[dim]Prompt:[/] {prompt}");
            AnsiConsole.WriteLine();

            var result = await AnsiConsole.Status()
                .StartAsync("[yellow]Processing email task...[/]", async ctx =>
                {
                    return await ExecuteReasoningTask(prompt);
                });

            AnsiConsole.Write(
                new Panel($"[green]Result:[/] {Markup.Escape(result)}")
                    .Header("Email Task Complete")
                    .Border(BoxBorder.Rounded)
                    .BorderColor(Color.Green));

            // Display token usage stats if using Response API
            var currentStats = GetCurrentReasonerStats();
            if (currentStats != null)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.Write(
                    new Panel($"[aqua]{currentStats}[/]")
                        .Header("Token Usage Statistics")
                        .Border(BoxBorder.Rounded)
                        .BorderColor(Color.Aqua));
            }
        });
    }

    /// <summary>
    /// Example 3: Demonstrate complex multi-step customer support workflow
    /// Shows advanced reasoning with multiple conditional steps and business logic
    /// </summary>
    private static async Task RunCustomerSupportWorkflowExample()
    {
        var supportRequest = """
        A customer john.smith@example.com contacted us saying they want to purchase a gaming laptop and it is entiled to a discount
        1. Check if they're in our customer database
        2. Send them information about our gaming laptop using 20% discount if existing customers, 10% if it is a new ones)
        """;
        await RunScenarioAsync("Customer Support Workflow", supportRequest, async () =>
        {
            AnsiConsole.Write(
                new Panel("[bold orange1]🧪 Test 3: Customer Support Workflow[/]\n[dim]Using: Direct OpenAI API[/]")
                    .Border(BoxBorder.Rounded)
                    .BorderColor(Color.Orange1));

            AnsiConsole.MarkupLine("[dim]Support Request:[/]");
            AnsiConsole.Write(
                new Panel(supportRequest)
                    .Border(BoxBorder.Rounded)
                    .BorderColor(Color.Grey));

            var result = await AnsiConsole.Status()
                .StartAsync("[orange1]Processing support workflow...[/]", async ctx =>
                {
                    return await ExecuteReasoningTask(supportRequest);
                });

            AnsiConsole.Write(
                new Panel($"[green]Final Result:[/] {Markup.Escape(result)}")
                    .Header("Customer Support Workflow Complete")
                    .Border(BoxBorder.Rounded)
                    .BorderColor(Color.Green));

            // Display token usage stats if using Response API
            var currentStats = GetCurrentReasonerStats();
            if (currentStats != null)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.Write(
                    new Panel($"[aqua]{currentStats}[/]")
                        .Header("Token Usage Statistics")
                        .Border(BoxBorder.Rounded)
                        .BorderColor(Color.Aqua));
            }

            // Show conversation log for this complex example
            AnsiConsole.WriteLine();
            AnsiConsole.Write(
                new Rule("[bold blue]Conversation Log[/]")
                    .RuleStyle("blue"));

            // Display key benefits
            AnsiConsole.WriteLine();
            var benefits = """
                [bold green]Direct OpenAI API Benefits:[/]
                • [yellow]Lower Overhead:[/] Uses the Azure OpenAI Response API directly
                • [yellow]Token Tracking:[/] Detailed per-step and cumulative token statistics
                • [yellow]Direct Control:[/] Full access to structured response options
                • [yellow]Predictable Behavior:[/] Strongly typed schemas drive tool calls
                • [yellow]Performance:[/] Fewer framework layers between prompt and model
                """;

            AnsiConsole.Write(
                new Panel(benefits)
                    .Header("Schema-Guided Reasoning Benefits")
                    .Border(BoxBorder.Rounded)
                    .BorderColor(Color.Green));
        });
    }

    /// <summary>
    /// Example 4: Demonstrate SQL database query with Excel export
    /// Creates a specialized reasoner with only SQL and Excel functions
    /// Shows how to use schema-guided reasoning for data extraction and export workflows
    /// </summary>
    private static async Task RunSqlExample(string userRequest)
    {
        await RunScenarioAsync("SQL Database + Excel Export", userRequest, async () =>
        {
            AnsiConsole.Write(
                new Panel("[bold aqua]🧪 Test 4: SQL Database + Excel Export[/]\n[dim]Using: Direct OpenAI API[/]")
                    .Border(BoxBorder.Rounded)
                    .BorderColor(Color.Aqua));

            AnsiConsole.MarkupLine("[yellow]Creating specialized reasoner with SQL and Excel functions only...[/]");
            AnsiConsole.WriteLine();

            // **Create a specialized reasoner that contains only SQL and Excel export functions**
            // This demonstrates how to compose custom reasoners for specific workflows
            var apiKey = GetRequiredSetting("OPENAI_API_KEY");
            var endpoint = GetRequiredSetting("AZURE_ENDPOINT");
            var deploymentId = "gpt-5-nano";
            var databaseService = new DatabaseService();
            var sqlServerService = new SqlServerService();

            // **Define the specific tool types for SQL workflow**
            var sqlToolTypes = new Type[]
            {
                typeof(ReportTaskCompletionToolCall),
                typeof(GetDatabaseNamesFromServerToolCall),
                typeof(GetSqlDatabaseSchemaToolCall),
                typeof(ExecuteSqlQueryToolCall),
                typeof(ExportSqlQueryResultToolCall)
            };

            // **Create a specialized BusinessFunctionFactory with only SQL-related functions**
            var sqlOpenAiConfiguration = new AzureOpenAiConfiguration(endpoint, apiKey, deploymentId);
            var loggerFactory = telemetry!.LoggerFactory;
            var sqlFunctionFactory = new BusinessFunctionFactory(databaseService, sqlServerService, sqlOpenAiConfiguration, loggerFactory, sqlToolTypes);

            // **Create custom options for SQL workflow**
            var sqlOptions = new SchemaGuidedReasonerOptions
            {
                JsonSerializerOptions = SchemaGuidedReasonerOptions.CreateDefaultJsonOptions(),
                SystemPromptBuilder = (context, _) =>
                {
                    var toolsSummary = SchemaGuidedReasonerSupport.GenerateToolsSummary(context.Schema.AvailableTools);
                    return $@"You are a SQL data analyst assistant.

IMPORTANT: You must always respond with structured JSON that includes:
1. Current state analysis
2. List of remaining steps to execute to complete the task
3. Whether the task is completed
4. Function to call, it contains parameter to execute the first remaining step

## IMPORTANT
- Please check carefully the status of the system to create the most relevant next function to call
- You should examine carefully what function were already called and their results
- Answer to the user only when you have the response to user question or you cannot proceed further

## Available Tools:
{toolsSummary}

## General rules
- If you want to check available databases use GetDatabaseNamesFromServer
- If you need to know table names and columns use GetSqlDatabaseSchema
- You can use the ExecuteSqlQuery function to execute a query expressed in natural language.
- If you need to export a query you need to execute first with ExecuteSqlQuery giving a resultId then call the exportSqlQueryResult function with the resultId obtained.
- When there are not anymore steps to execute, you can use the ReportTaskCompletion function to report the final result to the user. Tell if the process was successful or not.

";
                }
            };

            AnsiConsole.MarkupLine("[dim]User Request:[/]");
            AnsiConsole.Write(
                new Panel(userRequest)
                    .Border(BoxBorder.Rounded)
                    .BorderColor(Color.Grey));

            string result;
            var verboseOutput = GetCurrentReasonerVerboseOutput();

            if (selectedReasonerMode == PlaygroundReasonerMode.Forced)
            {
                var sqlReasoner = new ResponseApiForcedToolSchemaGuidedReasoner(
                    azureEndpoint: endpoint,
                    azureApiKey: apiKey,
                    deploymentId: deploymentId,
                    businessFunctionFactory: sqlFunctionFactory,
                    options: sqlOptions,
                    logger: telemetry!.LoggerFactory.CreateLogger<ResponseApiForcedToolSchemaGuidedReasoner>())
                {
                    VerboseOutput = verboseOutput,
#pragma warning disable OPENAI001
                    ReasoningEffortLevel = ResponseReasoningEffortLevel.Low
#pragma warning restore OPENAI001
                };

                result = await AnsiConsole.Status()
                    .StartAsync("[aqua]Processing SQL + Excel workflow...[/]", async ctx =>
                    {
                        return await sqlReasoner.ReasonAndActAsync(userRequest);
                    });

                AnsiConsole.WriteLine();
                AnsiConsole.Write(
                    new Panel($"[aqua]{sqlReasoner.CurrentSessionStats}[/]")
                        .Header("Token Usage Statistics")
                        .Border(BoxBorder.Rounded)
                        .BorderColor(Color.Aqua));
            }
            else
            {
                var sqlReasoner = new ResponseApiSchemaGuidedReasoner(
                    azureEndpoint: endpoint,
                    azureApiKey: apiKey,
                    deploymentId: deploymentId,
                    businessFunctionFactory: sqlFunctionFactory,
                    options: sqlOptions,
                    logger: telemetry!.LoggerFactory.CreateLogger<ResponseApiSchemaGuidedReasoner>())
                {
                    VerboseOutput = verboseOutput,
#pragma warning disable OPENAI001
                    ReasoningEffortLevel = ResponseReasoningEffortLevel.Low
#pragma warning restore OPENAI001
                };

                result = await AnsiConsole.Status()
                    .StartAsync("[aqua]Processing SQL + Excel workflow...[/]", async ctx =>
                    {
                        return await sqlReasoner.ReasonAndActAsync(userRequest);
                    });

                AnsiConsole.WriteLine();
                AnsiConsole.Write(
                    new Panel($"[aqua]{sqlReasoner.CurrentSessionStats}[/]")
                        .Header("Token Usage Statistics")
                        .Border(BoxBorder.Rounded)
                        .BorderColor(Color.Aqua));
            }

            AnsiConsole.Write(
                new Panel($"[green]Final Result:[/] {Markup.Escape(result)}")
                    .Header("SQL + Excel Workflow Complete")
                    .Border(BoxBorder.Rounded)
                    .BorderColor(Color.Green));

            // Show benefits of specialized reasoners
            AnsiConsole.WriteLine();
            AnsiConsole.Write(
                new Panel("""
                [bold green]Specialized Reasoner Benefits:[/]
                • [yellow]Focused Context:[/] Only SQL and Excel tools available, reducing complexity
                • [yellow]Custom Prompts:[/] Tailored system prompt for SQL workflow
                • [yellow]Type Safety:[/] Strongly typed SQL and Excel function calls
                • [yellow]Composability:[/] Easy to create domain-specific reasoners
                • [yellow]Multi-step Workflow:[/] Automatic orchestration of database discovery, querying, and export
                """)
                    .Header("Specialized SQL Reasoner")
                    .Border(BoxBorder.Rounded)
                    .BorderColor(Color.Green));
        });
    }

    private static async Task RunScenarioAsync(string scenarioName, string? userRequest, Func<Task> scenarioBody)
    {
        StateManager.Start();
        var scenarioStopwatch = Stopwatch.StartNew();
        var reasonerMode = selectedReasonerMode == PlaygroundReasonerMode.Forced
            ? "response-api-forced-tool"
            : "response-api";

        using var scenarioActivity = SgrTelemetry.StartScenarioActivity(
            scenarioName,
            reasonerMode,
            userRequest);

        var traceId = scenarioActivity?.TraceId.ToString();
        AnsiConsole.MarkupLine($"[grey]TraceId:[/] {traceId ?? "not available"}");
        logger?.LogInformation("Starting scenario {ScenarioName} with trace id {TraceId}", scenarioName, traceId);

        try
        {
            await scenarioBody();
            scenarioStopwatch.Stop();
            SgrTelemetry.RecordConversationCompleted(scenarioName, reasonerMode, scenarioStopwatch.Elapsed);
            logger?.LogInformation("Scenario {ScenarioName} completed", scenarioName);
        }
        catch (Exception ex)
        {
            scenarioStopwatch.Stop();
            SgrTelemetry.RecordConversationFailed(scenarioName, reasonerMode, scenarioStopwatch.Elapsed);
            SgrTelemetry.MarkError(scenarioActivity, ex);
            logger?.LogError(ex, "Scenario {ScenarioName} failed", scenarioName);
            throw;
        }
        finally
        {
            StateManager.Clear();
        }
    }

    private static string GetRequiredSetting(string key)
    {
        var value = Dotenv.Get(key);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        throw new InvalidOperationException($"Missing required configuration value '{key}'.");
    }

    private static string GetSelectedReasonerLabel()
    {
        return selectedReasonerMode == PlaygroundReasonerMode.Forced
            ? "Forced-tool Response API reasoner"
            : "Direct OpenAI API reasoner";
    }

    private static string? GetCurrentReasonerStats()
    {
        return selectedReasonerMode switch
        {
            PlaygroundReasonerMode.Forced => forcedResponseApiReasoner?.CurrentSessionStats.ToString(),
            _ => responseApiReasoner?.CurrentSessionStats.ToString()
        };
    }

    private static bool GetCurrentReasonerVerboseOutput()
    {
        return selectedReasonerMode switch
        {
            PlaygroundReasonerMode.Forced => forcedResponseApiReasoner?.VerboseOutput ?? false,
            _ => responseApiReasoner?.VerboseOutput ?? false
        };
    }
}
