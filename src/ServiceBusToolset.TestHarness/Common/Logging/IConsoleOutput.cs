namespace ServiceBusToolset.TestHarness.Common.Logging;

public interface IConsoleOutput
{
    void Info(string message);
    void Success(string message);
    void Warning(string message);
    void Error(string message);
    void Verbose(string message, bool isVerbose);
    void Progress(string message);
    void Table(IEnumerable<string> headers, IEnumerable<string[]> rows);
    string? ReadLine();
}
