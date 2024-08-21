using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Google;
using Microsoft.SemanticKernel.Connectors.MistralAI;
using Microsoft.SemanticKernel.Connectors.OpenAI;

#pragma warning disable SKEXP0070
IChatCompletionService chatService = new MistralAIChatCompletionService("open-mistral-7b", 
    Environment.GetEnvironmentVariable("API_KEY"));
#pragma warning restore SKEXP0070

ChatHistory history = new();
while (true)
{
    Console.Write("Ask your question: ");
    history.AddUserMessage(Console.ReadLine());

    var assistant = await chatService.GetChatMessageContentAsync(history);
    history.Add(assistant);
    Console.WriteLine(assistant);
}