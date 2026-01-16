namespace ServiceBusToolset.Services;

public interface IConsoleOutput
{
    void Info(string message);
    void Success(string message);
    void Warning(string message);
    void Error(string message);
    void Verbose(string message, bool isVerbose);
    void Progress(string message);
}
