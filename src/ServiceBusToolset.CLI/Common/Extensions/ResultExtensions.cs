using Ardalis.Result;

namespace ServiceBusToolset.CLI.Common.Extensions;

public static class ResultExtensions
{
    /// <summary>
    ///     Converts an IResult to Result&lt;TTarget&gt; while preserving error status and messages.
    ///     This is useful when you need to change the result type but want to maintain all error information.
    /// </summary>
    /// <param name="result">The result to convert.</param>
    /// <typeparam name="TTarget">The target type.</typeparam>
    /// <returns>The converted result.</returns>
    public static Result<TTarget> ToErrorResult<TTarget>(this IResult result)
        => result.Status.ToErrorResult<TTarget>(result.Errors, result.ValidationErrors);

    private static Result<T> ToErrorResult<T>(this ResultStatus status,
                                              IEnumerable<string> errors,
                                              IEnumerable<ValidationError> validationErrors)
    {
        return status switch
        {
            ResultStatus.Error => Result<T>.Error(new ErrorList(errors.ToArray())),
            ResultStatus.Forbidden => Result<T>.Forbidden(errors.ToArray()),
            ResultStatus.Unauthorized => Result<T>.Unauthorized(errors.ToArray()),
            ResultStatus.Invalid => Result<T>.Invalid(validationErrors.ToArray()),
            ResultStatus.NotFound => Result<T>.NotFound(errors.ToArray()),
            ResultStatus.Conflict => Result<T>.Conflict(errors.ToArray()),
            ResultStatus.CriticalError => Result<T>.CriticalError(errors.ToArray()),
            ResultStatus.Unavailable => Result<T>.Unavailable(errors.ToArray()),
            _ => throw new NotSupportedException($"Result {status} conversion is not supported.")
        };
    }
}
