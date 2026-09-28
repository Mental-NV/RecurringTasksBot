// Owned limit groups: Telegram message ceilings, generation execution
// budgets, and storage codec versions. Protocol ceilings are code
// constants, never JSON knobs; adjustable business/model budgets live in
// ExecutionOptions.
using System.Text;

namespace RecurringTasksBot.Application;

public static class TelegramLimits
{
    public const int RichTextChars = 32768;
    public const int PlainFallbackMaxUnits = 4096;
    public const int LiteralNewlineWindow = 512;
    // Telegram Bot API ceiling for documents sent by bots (50 MB).
    public const long DocumentMaxBytes = 50L * 1024 * 1024;

    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ProgressReserve = TimeSpan.FromSeconds(45);
}

public static class ExecutionLimits
{
    public const int AnswerSourceMaxScalars = 131072;
    public const int SystemInstructionMaxScalars = 16384;
    public const int MaxPlanLeaves = 128;
    public const string InstructionVersion = "recurring-task-v2";
    public const string ScheduleTimezone = "UTC";
    public const string EnabledCapabilities = "rich_text, formulas, details, links, unicode_symbols";

    public static readonly TimeSpan MaxLlmTimeout = TimeSpan.FromSeconds(510);
    public static readonly TimeSpan ActivityWorkBudget = TimeSpan.FromSeconds(540);
}

public static class StorageLimits
{
    public const int PropertyChunkScalars = 16000;
    // Consolidated storage schema version. Every persisted row carries
    // SchemaVersion = 1; anything else fails as an integrity error.
    public const int SchemaVersion = 1;
}
