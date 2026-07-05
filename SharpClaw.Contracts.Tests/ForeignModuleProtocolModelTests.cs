using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Modules.Foreign;
using SharpClaw.Contracts.Providers;
using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Contracts.Tests;

public sealed class ForeignModuleProtocolModelTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void ModuleHostCompileShapeUsesContractsOnly()
    {
        const string json = """
            {
              "id": "demo.module",
              "displayName": "Demo Module",
              "version": "1.0.0",
              "toolPrefix": "demo",
              "entryAssembly": "Demo.Module.dll",
              "minHostVersion": "0.1.0",
              "runtime": "dotnet",
              "hostMode": "sidecar"
            }
            """;

        var manifest = JsonSerializer.Deserialize<ModuleManifest>(json, JsonOptions)!;
        var runtime = ModuleManifestRuntimeInfo.FromJson(json);
        runtime.EnsureDotNetEntryAssembly(manifest);
        var addressEnv = ForeignModuleProtocol.ControlAddressEnv;
        var response = new ForeignModuleHandshakeResponse(
            ForeignModuleProtocol.Version,
            manifest.Id,
            manifest.ToolPrefix,
            runtime.Runtime,
            "10.0.9",
            [ForeignModuleCapability.HostCapabilities]);

        Assert.Equal("SHARPCLAW_CONTROL_ADDRESS", addressEnv);
        Assert.True(runtime.IsDotNet);
        Assert.True(runtime.IsSidecarHostMode);
        Assert.Equal("demo.module", response.ModuleId);
        Assert.Contains(ForeignModuleCapability.HostCapabilities, response.Capabilities!);
    }

    [Fact]
    public void HandshakeResponseRoundTripsWithWebJsonNames()
    {
        var response = new ForeignModuleHandshakeResponse(
            ForeignModuleProtocol.Version,
            "demo.module",
            "demo",
            ModuleManifestRuntimeInfo.DotNet,
            "10.0.9",
            [ForeignModuleCapability.Endpoints, ForeignModuleCapability.TaskRuntime]);

        var json = JsonSerializer.Serialize(response, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<ForeignModuleHandshakeResponse>(json, JsonOptions)!;

        Assert.Contains("\"protocolVersion\":1", json);
        Assert.Equal(response.ProtocolVersion, roundTrip.ProtocolVersion);
        Assert.Equal(response.ModuleId, roundTrip.ModuleId);
        Assert.Equal(response.ToolPrefix, roundTrip.ToolPrefix);
        Assert.Equal(response.Runtime, roundTrip.Runtime);
        Assert.Equal(response.RuntimeVersion, roundTrip.RuntimeVersion);
        Assert.Equal(response.Capabilities, roundTrip.Capabilities);
    }

    [Fact]
    public void TaskOperationExecutionRequestPreservesSnapshotAndVariables()
    {
        var instanceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var channelId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var request = new ForeignModuleTaskOperationExecutionRequest(
            ForeignModuleProtocol.Version,
            "demo.module",
            "demo.echo",
            new ForeignModuleTaskOperationExecutionContextSnapshot(
                instanceId,
                channelId,
                new Dictionary<string, JsonElement>
                {
                    ["count"] = JsonSerializer.SerializeToElement(3),
                },
                [new ForeignModuleTaskEventHandlerSnapshot("demo.event", "item", "handler-1")],
                "ctx-1"),
            ["hello"],
            "count",
            "result");

        var json = JsonSerializer.Serialize(request, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<ForeignModuleTaskOperationExecutionRequest>(json, JsonOptions)!;

        Assert.Equal("demo.echo", roundTrip.OperationKey);
        Assert.Equal(instanceId, roundTrip.Context.InstanceId);
        Assert.Equal(3, roundTrip.Context.Variables!["count"].GetInt32());
        Assert.Equal("handler-1", roundTrip.Context.EventHandlers![0].HandlerCallbackId);
        Assert.Equal("result", roundTrip.ResultVariable);
    }

    [Fact]
    public void DiscoveryResponseRoundTripsProtocolDescriptorModels()
    {
        var schema = JsonSerializer.SerializeToElement(new { type = "object" }, JsonOptions);
        var response = new ForeignModuleDiscoveryResponse(
            Tools:
            [
                new ForeignModuleToolDescriptor(
                    "echo",
                    "Echo input.",
                    schema,
                    new ForeignModulePermissionDescriptor(false, "CanEcho"),
                    TimeoutSeconds: 5,
                    Aliases: ["say"],
                    SupportsStreaming: true,
                    SupportsDynamicCompletionBehavior: true)
            ],
            TaskOperationExecutors:
            [
                new ForeignModuleTaskOperationExecutorDescriptor(
                    "demo.module",
                    ["demo.echo"],
                    SupportsInvocation: true)
            ],
            ProviderPlugins:
            [
                new ForeignModuleProviderPluginDescriptor(
                    "demo-provider",
                    "Demo Provider",
                    ParameterSpec: ForeignModuleCompletionParameterSpecDescriptor.From(
                        new TestCompletionParameterSpec()))
            ]);

        var json = JsonSerializer.Serialize(response, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<ForeignModuleDiscoveryResponse>(json, JsonOptions)!;

        Assert.Equal("echo", roundTrip.Tools![0].Name);
        Assert.Equal("CanEcho", roundTrip.Tools![0].Permission!.DelegateTo);
        Assert.True(roundTrip.TaskOperationExecutors![0].SupportsInvocation);
        Assert.Equal("Demo Provider", roundTrip.ProviderPlugins![0].ParameterSpec!.ProviderName);
        Assert.True(roundTrip.ProviderPlugins![0].ParameterSpec!.SupportsStrictTools);
    }

    [Fact]
    public void TaskStatementDescriptorProjectionPreservesNestedShape()
    {
        var nested = new TestTaskStatementInvocation("demo.child", RawExpression: "value");
        var statement = new TestTaskStatementInvocation(
            "demo.parent",
            VariableName: "item",
            ResultVariable: "result",
            Arguments: ["one", "two"],
            Body: [nested]);

        var descriptor = ForeignModuleTaskStatementInvocationDescriptor.From(statement);

        Assert.Equal("demo.parent", descriptor.StatementKey);
        Assert.Equal("item", descriptor.VariableName);
        Assert.Equal(["one", "two"], descriptor.Arguments);
        Assert.Equal("demo.child", descriptor.Body![0].StatementKey);
        Assert.Equal("value", descriptor.Body![0].RawExpression);
    }

    private sealed record TestTaskStatementInvocation(
        string StatementKey,
        string? VariableName = null,
        string? TypeName = null,
        string? ResultVariable = null,
        string? RawExpression = null,
        IReadOnlyList<string>? Arguments = null,
        string? ModuleTriggerKey = null,
        string? HandlerParameter = null,
        IReadOnlyList<ITaskStatementInvocation>? Body = null,
        IReadOnlyList<ITaskStatementInvocation>? ElseBody = null) : ITaskStatementInvocation;

    private sealed class TestCompletionParameterSpec : ICompletionParameterSpec
    {
        public string ProviderName => "Demo Provider";
        public bool SupportsTemperature => true;
        public float TemperatureMin => 0.1f;
        public float TemperatureMax => 1.1f;
        public bool SupportsTopP => true;
        public float TopPMin => 0.2f;
        public float TopPMax => 0.9f;
        public bool SupportsTopK => false;
        public int TopKMin => 1;
        public int TopKMax => 10;
        public bool SupportsFrequencyPenalty => true;
        public float FrequencyPenaltyMin => -1.0f;
        public float FrequencyPenaltyMax => 1.0f;
        public bool SupportsPresencePenalty => true;
        public float PresencePenaltyMin => -1.0f;
        public float PresencePenaltyMax => 1.0f;
        public bool SupportsStop => true;
        public int MaxStopSequences => 4;
        public bool SupportsSeed => false;
        public bool SupportsResponseFormat => true;
        public bool RejectsJsonObjectResponseFormat => true;
        public bool OnlyJsonObjectResponseFormat => false;
        public bool SupportsReasoningEffort => true;
        public bool ReasoningEffortInformationalOnly => false;
        public string[] ValidReasoningEffortValues => ["low", "high"];
        public bool SupportsToolChoice => true;
        public bool SupportsStrictTools => true;
    }
}
