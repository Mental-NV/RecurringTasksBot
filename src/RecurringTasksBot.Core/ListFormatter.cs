// /list rendering: one block per operation, packed into plain-text messages
// of at most MaxListMessageLength chars, keeping each operation together
// where possible. A single oversized operation is hard-split.
using System.Text;

namespace RecurringTasksBot.Core;

public sealed record OperationSummary(
    string OperationId,
    string CronExpression,
    string Text,
    OperationStatus Status);

public static class ListFormatter
{
    public const int MaxListMessageLength = 4096;

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
            foreach (var piece in block.Length <= maxLength
                         ? [block]
                         : Chunk(block, maxLength))
            {
                if (current.Length == 0)
                {
                    current.Append(piece);
                }
                else if (current.Length + 1 + piece.Length <= maxLength)
                {
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

    private static IEnumerable<string> Chunk(string text, int size)
    {
        for (var i = 0; i < text.Length; i += size)
            yield return text.Substring(i, Math.Min(size, text.Length - i));
    }

    private sealed class StringBuilderState
    {
        private readonly StringBuilder _sb = new();

        public StringBuilderState() { }

        public StringBuilderState(string initial) => _sb.Append(initial);

        public int Length => _sb.Length;

        public void Append(string s) => _sb.Append(s);

        public void Append(char c) => _sb.Append(c);

        public override string ToString() => _sb.ToString();
    }

    public static string EmptyListMessage =>
        "No reminders yet. Send /create <six-field schedule> <text>.";

    public static string HelpMessage =>
        "Hi! I send recurring reminders (UTC).\n" +
        "/create <sec min hour day month weekday> <text> — e.g. /create 0 0 9 * * * Water the plants (seconds must be 0)\n" +
        "/list — show your reminders\n" +
        "/delete <id> — delete a reminder";
}
