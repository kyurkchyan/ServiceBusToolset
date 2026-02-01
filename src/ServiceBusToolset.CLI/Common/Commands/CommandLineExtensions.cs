using CommandLine;

namespace ServiceBusToolset.CLI.Common.Commands;

public static class CommandLineExtensions
{
    public static Task<ParserResult<object>> WithCommandAsync<TCommand>(
        this ParserResult<object> parserResult,
        Func<TCommand, Task> parsedFunc) where TCommand : class =>
        parserResult.WithParsedAsync(parsedFunc);

    public static async Task<ParserResult<object>> WithCommandAsync<TCommand>(
        this Task<ParserResult<object>> parserResultTask,
        Func<TCommand, Task> parsedFunc) where TCommand : class
    {
        var parserResult = await parserResultTask;
        return await parserResult.WithParsedAsync(parsedFunc);
    }

    public static async Task WithNotParsedAsync(
        this Task<ParserResult<object>> parserResultTask,
        Func<IEnumerable<Error>, int> notParsedFunc)
    {
        var parserResult = await parserResultTask;
        parserResult.WithNotParsed(errors => notParsedFunc(errors));
    }
}
