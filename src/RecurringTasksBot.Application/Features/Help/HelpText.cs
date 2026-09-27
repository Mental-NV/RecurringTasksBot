// Help text: unknown, invalid, and missing commands answer with usage,
// including the external LLM/search disclosure.
namespace RecurringTasksBot.Application;

public static class HelpText
{
    public static string Message =>
        "Hi! I run your recurring prompts with an LLM web search (UTC).\n" +
        "/create <sec min hour day month weekday> <prompt> — e.g. /create 0 0 9 * * * Summarize today's AI news (seconds must be 0)\n" +
        "Reply to a long message with /create <schedule> to use it as the prompt (up to 32,768 characters).\n" +
        "/list — show your prompts\n" +
        "/delete <id> — delete a prompt\n" +
        "Each run executes the stored prompt fresh and sends the answer here. " +
        "Prompts are sent to an external LLM/search service.";
}
