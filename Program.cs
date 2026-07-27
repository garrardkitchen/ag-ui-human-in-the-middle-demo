#:sdk Microsoft.NET.Sdk.Web
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property PublishAot=false
#:package Azure.AI.Projects@2.1.0-beta.4
#:package Azure.Identity@1.21.0
#:package Microsoft.Agents.AI.Foundry@1.15.0-preview.260722.1
#:package Microsoft.Agents.AI.Hosting.AGUI.AspNetCore@1.15.0-preview.260722.1

using System.ComponentModel;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.AI.Projects;
using Azure.Core;
using Azure.Identity;
using AGUI.Server;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.Extensions.AI;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient().AddLogging();
builder.Services.AddAGUIServer();
builder.Services.AddDirectoryBrowser();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

var endpoint = app.Configuration["AZURE_AI_PROJECT_ENDPOINT"]
               ?? app.Configuration["AZURE_OPENAI_ENDPOINT"]
               ?? throw new InvalidOperationException("Set AZURE_AI_PROJECT_ENDPOINT or AZURE_OPENAI_ENDPOINT.");
var deploymentName = app.Configuration["AZURE_OPENAI_DEPLOYMENT_NAME"]
                     ?? throw new InvalidOperationException(
                         "Set AZURE_OPENAI_DEPLOYMENT_NAME to your Foundry model deployment name.");

[Description(
    "Return the computer hostname. This read-only operation still requires explicit human approval for the demo.")]
static string GetHostname() => Environment.MachineName;

static string MaskAfterFourCharacters(string value) =>
    value.Length <= 4 ? value : value[..4] + new string('*', value.Length - 4);

[Description(
    "Return the current Windows username. This read-only operation still requires explicit human approval for the demo.")]
static string GetUsername() => Environment.UserName;

#pragma warning disable MEAI001
AITool[] tools =
[
    new ApprovalRequiredAIFunction(AIFunctionFactory.Create(
        GetHostname, new AIFunctionFactoryOptions { Name = "GetHostname" })),
    new ApprovalRequiredAIFunction(AIFunctionFactory.Create(
        GetUsername, new AIFunctionFactoryOptions { Name = "GetUsername" }))
];
#pragma warning restore MEAI001

TokenCredential credential = app.Environment.IsDevelopment()
    ? new AzureCliCredential()
    : new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned);

var baseAgent = new AIProjectClient(new Uri(endpoint), credential)
    .AsAIAgent(
        model: deploymentName,
        name: "IdentityDemoAgent",
        instructions: "You are a deterministic demo assistant. The user chooses one of two options. " +
                      "If they ask for the hostname, call GetHostname. If they ask for the username, call GetUsername. " +
                      "Do not call both tools. After the approved tool returns, answer with only the value and a short label.",
        tools: tools);

// The middleware translates ApprovalRequiredAIFunction messages into AG-UI request_approval client tool calls.
#pragma warning disable MEAI001
var agent = baseAgent.AsBuilder()
    .Use(runFunc: null, runStreamingFunc: (messages, session, options, innerAgent, cancellationToken) =>
        HandleApprovalRequestsMiddleware(messages, session, options, innerAgent, cancellationToken))
    .Build();
#pragma warning restore MEAI001

app.MapAGUIServer("/agent", agent);
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

await app.RunAsync();

#pragma warning disable MEAI001
static async IAsyncEnumerable<AgentResponseUpdate> HandleApprovalRequestsMiddleware(
    IEnumerable<ChatMessage> messages,
    AgentSession? session,
    AgentRunOptions? options,
    AIAgent innerAgent,
    [EnumeratorCancellation] CancellationToken cancellationToken)
{
    var approvalDecision = TryTakeApprovalDecision(messages);
    if (approvalDecision is not null)
    {
        var text = approvalDecision.Response.Approved
            ? approvalDecision.Request.FunctionName switch
            {
                "GetHostname" => $"Hostname: {MaskAfterFourCharacters(GetHostname())}",
                "GetUsername" => $"Username: {GetUsername()}",
                _ => "Unknown approved function."
            }
            : "Request rejected.";

        yield return new AgentResponseUpdate(ChatRole.Assistant, [new TextContent(text)]);
        yield break;
    }

    await foreach (var update in innerAgent.RunStreamingAsync(
                       messages, session, options, cancellationToken))
    {
        await foreach (var processedUpdate in ConvertFunctionApprovalsToToolCalls(update))
        {
            yield return processedUpdate;
        }
    }

    static ApprovalDecision? TryTakeApprovalDecision(
        IEnumerable<ChatMessage> messages)
    {
        var approvalToolCalls = new Dictionary<string, FunctionCallContent>();
        FunctionResultContent? approvalResult = null;

        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is FunctionCallContent { Name: "request_approval" } toolCall)
                {
                    approvalToolCalls[toolCall.CallId] = toolCall;
                }
                else if (content is FunctionResultContent result && approvalToolCalls.ContainsKey(result.CallId))
                {
                    approvalResult = result;
                }
            }
        }

        if (approvalResult is null || !approvalToolCalls.TryGetValue(approvalResult.CallId, out var approvalToolCall))
        {
            return null;
        }

        ApprovalResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<ApprovalResponse>(approvalResult.Result?.ToString() ?? "{}");
        }
        catch (JsonException)
        {
            return null;
        }

        if (response is null ||
            response.ApprovalId != approvalToolCall.CallId ||
            !PendingApprovals.Requests.TryRemove(response.ApprovalId, out var request))
        {
            return null;
        }

        return new ApprovalDecision(request, response);
    }

    static async IAsyncEnumerable<AgentResponseUpdate> ConvertFunctionApprovalsToToolCalls(
        AgentResponseUpdate update)
    {
        var approvalRequest = update.Contents.OfType<ToolApprovalRequestContent>().FirstOrDefault();
        if (approvalRequest is null)
        {
            yield return update;
            yield break;
        }

        var functionCall = approvalRequest.ToolCall as FunctionCallContent
                           ?? throw new InvalidOperationException(
                               "Expected a function tool call in the approval request.");
        var approvalData = new ApprovalRequest
        {
            ApprovalId = approvalRequest.RequestId,
            FunctionCallId = functionCall.CallId,
            FunctionName = functionCall.Name,
            FunctionArguments = functionCall.Arguments is null
                ? null
                : JsonSerializer.SerializeToElement(functionCall.Arguments),
            Message = $"Approve execution of '{functionCall.Name}'?"
        };
        PendingApprovals.Requests[approvalData.ApprovalId] = approvalData;

        yield return new AgentResponseUpdate(ChatRole.Assistant,
        [
            new FunctionCallContent(
                callId: approvalRequest.RequestId,
                name: "request_approval",
                arguments: new Dictionary<string, object?>
                {
                    ["request"] = JsonSerializer.Serialize(approvalData)
                })
        ]);
    }
}
#pragma warning restore MEAI001

public sealed class ApprovalRequest
{
    [JsonPropertyName("approval_id")] public required string ApprovalId { get; init; }
    [JsonPropertyName("function_call_id")] public required string FunctionCallId { get; init; }
    [JsonPropertyName("function_name")] public required string FunctionName { get; init; }

    [JsonPropertyName("function_arguments")]
    public JsonElement? FunctionArguments { get; init; }

    [JsonPropertyName("message")] public string? Message { get; init; }
}

public sealed class ApprovalResponse
{
    [JsonPropertyName("approval_id")] public required string ApprovalId { get; init; }
    [JsonPropertyName("approved")] public required bool Approved { get; init; }
}

public sealed record ApprovalDecision(ApprovalRequest Request, ApprovalResponse Response);

public static class PendingApprovals
{
    public static ConcurrentDictionary<string, ApprovalRequest> Requests { get; } = new();
}
