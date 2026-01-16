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
}
