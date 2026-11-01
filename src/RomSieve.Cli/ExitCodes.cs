namespace RomSieve.Cli;

/// <summary>Exit codes of the command line, documented in docs/en/command-line.md and docs/fr/command-line.md.</summary>
public static class ExitCodes
{
    /// <summary>The command succeeded.</summary>
    public const int Success = 0;

    /// <summary>Unexpected error.</summary>
    public const int UnexpectedError = 1;

    /// <summary>Invalid arguments: unknown option, missing or invalid value, incompatible options.</summary>
    public const int InvalidArguments = 2;

    /// <summary>Invalid configuration: configuration file or profile missing or unreadable, source, DAT or destination not found, destination overlapping the source.</summary>
    public const int InvalidConfiguration = 3;

    /// <summary>The run ended, but some files are in error, an export failed, some operations could not be undone, or some items of the quarantine could not be deleted.</summary>
    public const int CompletedWithErrors = 4;

    /// <summary>The run was cancelled (Ctrl+C).</summary>
    public const int Cancelled = 5;

    /// <summary>A destructive operation needs a confirmation: <c>--yes</c> (also to empty the quarantine), or <c>--append</c> / <c>--purge --yes</c> for a non-empty destination.</summary>
    public const int ConfirmationRequired = 6;

    /// <summary>There is no sort to undo.</summary>
    public const int NothingToUndo = 7;
}
