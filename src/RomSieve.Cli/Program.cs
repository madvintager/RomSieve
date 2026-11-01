using System.Text;
using RomSieve.Cli;

// Accented messages must not be garbled by the code page of a Windows console
Console.OutputEncoding = new UTF8Encoding(false);
return await new CliApplication(Console.Out, Console.Error).RunAsync(args);
