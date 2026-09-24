// /list rendering: one block per operation, packed into rich messages of at
// most MaxListMessageLength characters, keeping each operation together
// where possible. A single oversized operation is hard-split. Character
// counts use Unicode scalar values so emoji and other multi-unit characters
// are never separated.
using System.Text;

namespace RecurringTasksBot.Core;

public sealed record OperationSummary(
    string OperationId,
    string CronExpression,
    string Text,
    OperationStatus Status);

public static class ListFormatter
{
    public const int MaxListMessageLength = TextLimits.MaxListMessageChars;

    public static string FormatBlock(OperationSummary op) =>
        $"{op.OperationId}\nSchedule: {op.CronExpression} (UTC)\nStatus: {OperationStatusNames.ToName(op.Status)}\n{op.Text}";

    public static IReadOnlyList<string> Split(
        IEnumerable<OperationSummary> operations,
        int maxLength = MaxListMessageLength)
    {
        if (maxLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxLength));

        var messages = new List<string>();
        var current = new StringBuilderState();

        foreach (var op in operations)
        {
            var block = FormatBlock(op);
            foreach (var piece in TextLimits.CountChars(block) <= maxLength
                         ? [block]
                         : TextLimits.SplitByChars(block, maxLength))
            {
                if (current.Length == 0)
                {
                    current.Append(piece);
                }
                else if (current.Length + 1 + TextLimits.CountChars(piece) <= maxLength)
                {
                    // Length tracks characters, not UTF-16 units.
                    current.Append('\n');
                    current.Append('\n');
                    current.Append(piece);
                }
                else
                {
                    messages.Add(current.ToString());
                    current = new StringBuilderState(piece);
                }
            }
        }

        if (current.Length > 0)
            messages.Add(current.ToString());

        return messages;
    }

    private sealed class StringBuilderState
    {
        private readonly StringBuilder _sb = new();
        private int _chars;

        public StringBuilderState() { }

        public StringBuilderState(string initial)
        {
            _sb.Append(initial);
            _chars = TextLimits.CountChars(initial);
        }

        public int Length => _chars;

        public void Append(string s)
        {
            _sb.Append(s);
            _chars += TextLimits.CountChars(s);
        }

        public void Append(char c)
        {
            _sb.Append(c);
            _chars += TextLimits.CountChars(c.ToString());
        }

        public override string ToString() => _sb.ToString();
    }

    public static string EmptyListMessage =>
        "No recurring prompts yet. Send /create <six-field schedule> <prompt>.";

    public static string HelpMessage =>
        "Hi! I run your recurring prompts with an LLM web search (UTC).\n" +
        "/create <sec min hour day month weekday> <prompt> — e.g. /create 0 0 9 * * * Summarize today's AI news (seconds must be 0)\n" +
        "Reply to a long message with /create <schedule> to use it as the prompt (up to 32,768 characters).\n" +
        "/list — show your prompts\n" +
        "/delete <id> — delete a prompt\n" +
        "Each run executes the stored prompt fresh and sends the answer here. " +
        "Prompts are sent to an external LLM/search service.";
}
