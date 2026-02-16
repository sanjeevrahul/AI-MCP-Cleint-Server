﻿using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.AI.Inference;
using ModelContextProtocol;

Console.WriteLine(Environment.GetEnvironmentVariable("GITHUB_TOKEN") != null
    ? "Token found"
    : "Token missing");
var endpoint = "https://models.inference.ai.azure.com";
var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN"); // Your GitHub Access Token
if (string.IsNullOrWhiteSpace(token))
{
    Console.WriteLine("Please set the GITHUB_TOKEN environment variable to your GitHub Access Token.");
    return;
}

var client = new ChatCompletionsClient(new Uri(endpoint), new AzureKeyCredential(token));
var chatHistory = new List<ChatRequestMessage>
{
    new ChatRequestSystemMessage("You are a helpful assistant that knows about AI")
};

var clientTransport = new StdioClientTransport(new StdioClientTransportOptions
{
    Command = @"C:\Program Files\dotnet\dotnet.exe",
    Arguments =
    [
        "run",
        "--project",
        @"C:\MCP\McpCalculatorServer\McpCalculatorServer.csproj"
    ]
});

Console.WriteLine("Setting up stdio transport");

await using var mcpClient = await  McpClient.CreateAsync(clientTransport);


ChatCompletionsToolDefinition ConvertFrom(string name, string description, JsonElement jsonElement)
{ 
    // convert the tool to a function definition
    FunctionDefinition functionDefinition = new(name)
    {
        Description = description,
        Parameters = BinaryData.FromObjectAsJson(new
        {
            Type = "object",
            Properties = jsonElement
        },
        new JsonSerializerOptions() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
    };

    // create a tool definition
    ChatCompletionsToolDefinition toolDefinition = new(functionDefinition);
    return toolDefinition;
}

async Task<List<ChatCompletionsToolDefinition>> GetMcpTools()
{
    Console.WriteLine("Listing tools");
    var tools = await mcpClient.ListToolsAsync();

    List<ChatCompletionsToolDefinition> toolDefinitions = [];

    foreach (var tool in tools)
    {
        Console.WriteLine($"Connected to server with tools: {tool.Name}");
        Console.WriteLine($"Tool description: {tool.Description}");
        Console.WriteLine($"Tool parameters: {tool.JsonSchema}");

        tool.JsonSchema.TryGetProperty("properties", out JsonElement propertiesElement);

        var def = ConvertFrom(tool.Name, tool.Description, propertiesElement);
        Console.WriteLine($"Tool definition: {def}");
        toolDefinitions.Add(def); 

        Console.WriteLine($"Properties: {propertiesElement}");        
    }



    return toolDefinitions;
}

// 1. List tools on mcp server

var tools = await GetMcpTools();
for (int i = 0; i < tools.Count; i++)
{
    var tool = tools[i];
    Console.WriteLine($"MCP Tools def: {i}: {tool}");
}

// 2. Define the chat history and the user message
Console.WriteLine("Enter a message for the assistant:");
string? userMessage=Console.ReadLine();
//var userMessage = "sum 2 and 4";
Console.WriteLine(userMessage);
chatHistory.Add(new ChatRequestUserMessage(userMessage));

// 3. Define options, including the tools
var options = new ChatCompletionsOptions(chatHistory)
{
    Model = "gpt-4.1-mini",
     Tools = { tools[0], tools[1], tools[2] }
};

// 4. Call the model

ChatCompletions? response = await client.CompleteAsync(options);
var content = response.Content;

// 5. Check if the response contains a function call
ChatCompletionsToolCall? calls = response.ToolCalls.FirstOrDefault();
for (int i = 0; i < response.ToolCalls.Count; i++)
{
    var call = response.ToolCalls[i];
    Console.WriteLine($"Tool call {i}: {call.Name} with arguments {call.Arguments}");
    //Tool call 0: add with arguments {"a":2,"b":4}

    var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(call.Arguments);
    var result = await mcpClient.CallToolAsync(
        call.Name,
        dict!,
        cancellationToken: CancellationToken.None
    );

var text = result.Content
                 .OfType<TextContentBlock>()
                 .FirstOrDefault()?.Text;

Console.WriteLine(text);


}

// 6. Print the generic response
Console.WriteLine($"Assistant response: {content}");
// Console.WriteLine($"Function call: {functionCall?.Name}");

// check if tool call, if so, call the tool