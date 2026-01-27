using Azure.Core;
using Azure.Identity;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using ServiceBusToolset.Models;

namespace ServiceBusToolset.Services;

public class AppInsightsService : IAppInsightsService
{
    private LogsQueryClient? _logsClient;
    private ResourceIdentifier? _resourceId;

    public void Initialize(string appInsightsResourceId)
    {
        _resourceId = new ResourceIdentifier(appInsightsResourceId);
        _logsClient = new LogsQueryClient(new DefaultAzureCredential());
    }

    public async Task<DiagnosticResult> DiagnoseMessageAsync(
        string operationId,
        DateTimeOffset enqueuedTime,
        CancellationToken cancellationToken)
    {
        if (_logsClient == null || _resourceId is null)
        {
            throw new InvalidOperationException("Service not initialized. Call Initialize first.");
        }

        var result = new DiagnosticResult
        {
            OperationId = operationId,
            EnqueuedTime = enqueuedTime
        };

        // Query time range: from a bit before enqueue time to capture the processing attempts
        var startTime = enqueuedTime.AddHours(-1);
        var endTime = enqueuedTime.AddHours(24);
        var timeRange = new QueryTimeRange(startTime, endTime);

        // Query exceptions
        result.Exceptions = await QueryExceptionsAsync(operationId, timeRange, cancellationToken);

        // Query traces (warnings and errors)
        result.Traces = await QueryTracesAsync(operationId, timeRange, cancellationToken);

        // Query failed dependencies
        result.FailedDependencies = await QueryDependenciesAsync(operationId, timeRange, cancellationToken);

        return result;
    }

    private async Task<List<ExceptionInfo>> QueryExceptionsAsync(
        string operationId,
        QueryTimeRange timeRange,
        CancellationToken cancellationToken)
    {
        var exceptions = new List<ExceptionInfo>();
        var query = $@"
exceptions
| where operation_Id == '{operationId}'
| order by timestamp desc
| take 20
| project timestamp, problemId, type, outerMessage, innermostMessage, details
";

        try
        {
            var response = await _logsClient!.QueryResourceAsync(_resourceId,
                                                                 query,
                                                                 timeRange,
                                                                 cancellationToken:cancellationToken);

            if (response.Value.Status == LogsQueryResultStatus.Success)
            {
                var table = response.Value.Table;
                var columns = table.Columns.Select(c => c.Name).ToList();

                foreach (var row in table.Rows)
                {
                    exceptions.Add(new ExceptionInfo
                    {
                        Timestamp = GetValue<DateTimeOffset>(row, columns, "timestamp"),
                        ProblemId = GetValue<string>(row, columns, "problemId"),
                        ExceptionType = GetValue<string>(row, columns, "type"),
                        OuterMessage = GetValue<string>(row, columns, "outerMessage"),
                        InnermostMessage = GetValue<string>(row, columns, "innermostMessage"),
                        Details = GetValue<string>(row, columns, "details")
                    });
                }
            }
        }
        catch
        {
            // Query failed, return empty list
        }

        return exceptions;
    }

    private async Task<List<TraceInfo>> QueryTracesAsync(
        string operationId,
        QueryTimeRange timeRange,
        CancellationToken cancellationToken)
    {
        var traces = new List<TraceInfo>();
        var query = $@"
traces
| where operation_Id == '{operationId}'
| where severityLevel >= 2
| order by timestamp desc
| take 20
| project timestamp, message, severityLevel
";

        try
        {
            var response = await _logsClient!.QueryResourceAsync(_resourceId,
                                                                 query,
                                                                 timeRange,
                                                                 cancellationToken:cancellationToken);

            if (response.Value.Status == LogsQueryResultStatus.Success)
            {
                var table = response.Value.Table;
                var columns = table.Columns.Select(c => c.Name).ToList();

                foreach (var row in table.Rows)
                {
                    traces.Add(new TraceInfo
                    {
                        Timestamp = GetValue<DateTimeOffset>(row, columns, "timestamp"),
                        Message = GetValue<string>(row, columns, "message"),
                        SeverityLevel = GetValue<int>(row, columns, "severityLevel")
                    });
                }
            }
        }
        catch
        {
            // Query failed, return empty list
        }

        return traces;
    }

    private async Task<List<DependencyInfo>> QueryDependenciesAsync(
        string operationId,
        QueryTimeRange timeRange,
        CancellationToken cancellationToken)
    {
        var dependencies = new List<DependencyInfo>();
        var query = $@"
dependencies
| where operation_Id == '{operationId}'
| where success == false
| order by timestamp desc
| take 10
| project timestamp, type, target, name, data, resultCode, success, duration
";

        try
        {
            var response = await _logsClient!.QueryResourceAsync(_resourceId,
                                                                 query,
                                                                 timeRange,
                                                                 cancellationToken:cancellationToken);

            if (response.Value.Status == LogsQueryResultStatus.Success)
            {
                var table = response.Value.Table;
                var columns = table.Columns.Select(c => c.Name).ToList();

                foreach (var row in table.Rows)
                {
                    dependencies.Add(new DependencyInfo
                    {
                        Timestamp = GetValue<DateTimeOffset>(row, columns, "timestamp"),
                        Type = GetValue<string>(row, columns, "type"),
                        Target = GetValue<string>(row, columns, "target"),
                        Name = GetValue<string>(row, columns, "name"),
                        Data = GetValue<string>(row, columns, "data"),
                        ResultCode = GetValue<int>(row, columns, "resultCode"),
                        Success = GetValue<bool>(row, columns, "success"),
                        DurationMs = GetValue<double>(row, columns, "duration")
                    });
                }
            }
        }
        catch
        {
            // Query failed, return empty list
        }

        return dependencies;
    }

    private static T GetValue<T>(LogsTableRow row, List<string> columns, string columnName)
    {
        var index = columns.IndexOf(columnName);
        if (index < 0 || row[index] == null)
        {
            return default!;
        }

        var value = row[index];

        if (typeof(T) == typeof(string))
        {
            return (T)(object)(value?.ToString() ?? string.Empty);
        }

        if (typeof(T) == typeof(DateTimeOffset) && value is DateTimeOffset dto)
        {
            return (T)(object)dto;
        }

        if (typeof(T) == typeof(int))
        {
            return (T)(object)Convert.ToInt32(value);
        }

        if (typeof(T) == typeof(double))
        {
            return (T)(object)Convert.ToDouble(value);
        }

        if (typeof(T) == typeof(bool))
        {
            return (T)(object)Convert.ToBoolean(value);
        }

        return default!;
    }
}
