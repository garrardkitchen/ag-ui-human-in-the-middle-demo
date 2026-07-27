# AG-UI hostname / username file-based app

A single-file .NET 10 ASP.NET Core app demonstrating:

- Microsoft Agent Framework hosted through `MapAGUI`.
- Server-Sent Events (SSE) streaming.
- Human-in-the-loop approval with `ApprovalRequiredAIFunction`.
- Two browser choices: hostname or Windows username.
- Azure AI Foundry / Azure OpenAI authentication using Azure CLI locally and Managed Identity when deployed.

## Prerequisites

- .NET 10 SDK or later (file-based apps require .NET 10).
- An Azure AI Foundry project or Azure OpenAI resource with an OpenAI model deployment.
- Azure CLI installed and authenticated: `az login`.
- Your identity has permission to invoke the model (for Azure OpenAI, typically `Cognitive Services OpenAI Contributor`).

When the application runs in the `Development` environment, it authenticates with
the account selected by `az login`. In other environments, it uses the Azure host's
Managed Identity, which must be enabled and granted permission to invoke the model.

## Configure

From this directory, set the deployment name and endpoint. For a Foundry project endpoint:

```bash
export AZURE_AI_PROJECT_ENDPOINT="https://<resource>.services.ai.azure.com/api/projects/<project>"
export AZURE_OPENAI_DEPLOYMENT_NAME="<your-model-deployment-name>"
```

If you are using a direct Azure OpenAI resource endpoint instead, use:

```bash
export AZURE_OPENAI_ENDPOINT="https://<resource>.openai.azure.com/"
export AZURE_OPENAI_DEPLOYMENT_NAME="<your-model-deployment-name>"
```

On Windows PowerShell, the equivalent is:

```powershell
$env:AZURE_AI_PROJECT_ENDPOINT = "https://<resource>.services.ai.azure.com/api/projects/<project>"
$env:AZURE_OPENAI_DEPLOYMENT_NAME = "<your-model-deployment-name>"
```

## Run

```bash
dotnet run Program.cs -- --urls http://localhost:8888
```

Open <http://localhost:8888> and choose one of the two options. The agent calls the matching approval-required function. The page displays the AG-UI approval request; select **Approve** or **Reject**. On approval, the server returns the actual machine value.

The raw AG-UI endpoint is `POST /agent`; a health check is `GET /health`.

## Notes

`Program.cs` is a .NET 10 file-based app: its Web SDK, target framework, and NuGet
dependencies are declared with `#:` directives at the top of the file, so no
`.csproj` is required.

The AG-UI and Agent Framework packages are prerelease packages, as required by the current Microsoft Learn integration guidance.
