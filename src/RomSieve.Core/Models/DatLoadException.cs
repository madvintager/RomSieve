namespace RomSieve.Core.Models;

/// <summary>
/// The DAT of a run could not be read: a folder or an archive of DATs refused (<see cref="DatFolderException"/>), or a
/// DAT that cannot be parsed. Its message is translated and names the DAT; the command line answers it as an invalid
/// configuration, not as a crash.
/// </summary>
public sealed class DatLoadException(string message, Exception innerException) : InvalidOperationException(message, innerException);
