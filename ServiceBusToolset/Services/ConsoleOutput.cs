using Spectre.Console;

namespace ServiceBusToolset.Services;

public class ConsoleOutput : IConsoleOutput
{
    public void Info(string message)
    {
        Console.WriteLine(message);
    }

    public void Success(string message)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    public void Warning(string message)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    public void Error(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine(message);
        Console.ResetColor();
    }

    public void Verbose(string message, bool isVerbose)
    {
        if (isVerbose)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine(message);
            Console.ResetColor();
        }
    }

    public void Progress(string message)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write($"\r{message}");
        Console.ResetColor();
    }

    public void Table(IEnumerable<string> headers, IEnumerable<string[]> rows)
    {
        var table = new Table();
        table.Border(TableBorder.Rounded);
        table.Expand();

        foreach (var header in headers)
        {
            var column = new TableColumn(header) { NoWrap = false };
            table.AddColumn(column);
        }

        foreach (var row in rows)
        {
            table.AddRow(row.Select(Markup.Escape).ToArray());
        }

        AnsiConsole.Write(table);
    }

    public string? ReadLine() => Console.ReadLine();
}
