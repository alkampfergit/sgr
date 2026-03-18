using Alkampfer.Sgr.BusinessFunctions;
using Alkampfer.Sgr.Models;
using Alkampfer.Sgr.Utils;
using System.Text.Json.Nodes;
using ActualForcedReasonerNextStep = Alkampfer.Sgr.Models.ForcedReasonerNextStep;
using ActualToolCall = Alkampfer.Sgr.Models.ToolCall;

namespace Alkampfer.Sgr.Tests;

[TestFixture]
public class ForcedReasonerNextStepTests
{
    private PolymorphicSchemaManager<ActualForcedReasonerNextStep, ActualToolCall> _manager = null!;

    [SetUp]
    public void Setup()
    {
        _manager = new PolymorphicSchemaManager<ActualForcedReasonerNextStep, ActualToolCall>()
            .AddDerivedType<SendEmailToolCall>()
            .AddDerivedType<ReportTaskCompletionToolCall>();
    }

    [Test]
    public void GenerateSchema_ShouldMarkStructuredPlanStepPropertiesAsRequired()
    {
        var schemaJson = _manager.GenerateSchema();
        var schemaNode = JsonNode.Parse(schemaJson);

        Assert.That(schemaNode, Is.Not.Null, "Generated schema should be valid JSON");

        var root = schemaNode!.AsObject();
        var properties = root["properties"]?.AsObject();
        Assert.That(properties, Is.Not.Null, "Schema should contain properties");

        var planProperty = properties!["PlanRemainingStepsBrief"]?.AsObject();
        Assert.That(planProperty, Is.Not.Null, "Schema should contain PlanRemainingStepsBrief");

        var itemSchema = planProperty!["item"]?.AsObject();
        Assert.That(itemSchema, Is.Not.Null, "PlanRemainingStepsBrief should define an item schema");

        var itemProperties = itemSchema!["properties"]?.AsObject();
        Assert.That(itemProperties, Is.Not.Null, "Plan step item schema should contain properties");
        Assert.That(itemProperties!.ContainsKey("description"), Is.True, "Plan step should contain description");
        Assert.That(itemProperties.ContainsKey("toolid"), Is.True, "Plan step should contain toolid");

        var required = itemSchema["required"]?.AsArray();
        Assert.That(required, Is.Not.Null, "Plan step item schema should define required properties");
        Assert.That(required!.Select(n => n?.GetValue<string>()).ToArray(), Does.Contain("description"));
        Assert.That(required.Select(n => n?.GetValue<string>()).ToArray(), Does.Contain("toolid"));
    }

    [Test]
    public void DeserializeFromJson_ShouldDeserializeStructuredPlanStepsAndToolCall()
    {
        var json = """
        {
            "CurrentState": "Need to send an email to the customer",
            "PlanRemainingStepsBrief": [
                {
                    "description": "Send the customer the requested update",
                    "toolid": "send_email_tool_call"
                },
                {
                    "description": "Report completion",
                    "toolid": "report_task_completion_tool_call"
                }
            ],
            "TaskCompleted": false,
            "Function": {
                "type": "send_email_tool_call",
                "subject": "Update",
                "message": "Here is the latest update.",
                "recipientEmail": "customer@example.com",
                "files": []
            }
        }
        """;

        var result = _manager.DeserializeFromJson(json);

        Assert.That(result, Is.Not.Null, "Structured planning payload should deserialize");
        Assert.That(result!.CurrentState, Is.EqualTo("Need to send an email to the customer"));
        Assert.That(result.TaskCompleted, Is.False);
        Assert.That(result.PlanRemainingStepsBrief, Has.Count.EqualTo(2));
        Assert.That(result.PlanRemainingStepsBrief[0].Description, Is.EqualTo("Send the customer the requested update"));
        Assert.That(result.PlanRemainingStepsBrief[0].ToolId, Is.EqualTo("send_email_tool_call"));
        Assert.That(result.Function, Is.InstanceOf<SendEmailToolCall>());

        var tool = (SendEmailToolCall)result.Function;
        Assert.That(tool.Subject, Is.EqualTo("Update"));
        Assert.That(tool.RecipientEmail, Is.EqualTo("customer@example.com"));
    }
}
