using Azure.Core;
using Azure.Identity;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using ServiceBusToolset.Application.DeadLetters.DiagnoseDlq.Models;

namespace ServiceBusToolset.Application.DeadLetters.DiagnoseDlq.Common.AppInsights;

public class AppInsightsService : IAppInsightsService
{
    private const int BatchSize = 100;
    private LogsQueryClient? _logsClient;
    private ResourceIdentifier? _resourceId;

    public void Initialize(string appInsightsResourceId)
    {
        _resourceId = new ResourceIdentifier(appInsightsResourceId);
        _logsClient = new LogsQueryClient(new DefaultAzureCredential());
    }

    public async Task<Dictionary<string, DiagnosticResult>> DiagnoseBatchAsync(
        IReadOnlyList<(string OperationId, DateTimeOffset EnqueuedTime)> operations,
        Action<int, int>? onProgress,
        CancellationToken cancellationToken)
    {
        if (_logsClient == null || _resourceId is null)
        {
            throw new InvalidOperationException("Service not initialized. Call Initialize first.");
        }

        var results = new Dictionary<string, DiagnosticResult>();

        // Initialize results for all operation IDs
        foreach (var (operationId, enqueuedTime) in operations)
        {
            results[operationId] = new DiagnosticResult
            {
                OperationId = operationId,
                EnqueuedTime = enqueuedTime
            };
        }

        // Calculate time range covering all messages
        var minTime = operations.Min(o => o.EnqueuedTime).AddHours(-1);
        var maxTime = operations.Max(o => o.EnqueuedTime).AddHours(24);
        var timeRange = new QueryTimeRange(minTime, maxTime);

        // Process in batches
        var operationIds = operations.Select(o => o.OperationId).ToList();
        var totalBatches = (int)Math.Ceiling(operationIds.Count / (double)BatchSize);
        var currentBatch = 0;

        for (var i = 0; i < operationIds.Count; i += BatchSize)
        {
            currentBatch++;
            onProgress?.Invoke(currentBatch, totalBatches);

            var batch = operationIds.Skip(i).Take(BatchSize).ToList();
            var operationIdList = string.Join("', '", batch);

            // Query exceptions for batch
            await QueryExceptionsBatchAsync(operationIdList,
                                            timeRange,
                                            results,
                                            cancellationToken);

            // Query traces for batch
            await QueryTracesBatchAsync(operationIdList,
                                        timeRange,
                                        results,
                                        cancellationToken);

            // Query dependencies for batch
            await QueryDependenciesBatchAsync(operationIdList,
                                              timeRange,
                                              results,
                                              cancellationToken);
        }

        return results;
    }

    private async Task QueryExceptionsBatchAsync(string operationIdList,
                                                 QueryTimeRange timeRange,
                                                 Dictionary<string, DiagnosticResult> results,
                                                 CancellationToken cancellationToken)
    {
        var query = $"""

                     exceptions
                     | where operation_Id in ('{operationIdList}')
                     | order by timestamp desc
                     | project operation_Id, timestamp, problemId, type, outerMessage, innermostMessage, details

                     """;

        try
        {
            var response = await _logsClient!.QueryResourceAsync(_resourceId,
                                                                 query,
                                                                 timeRange,
                                                                 cancellationToken: cancellationToken);

            if (response.Value.Status == LogsQueryResultStatus.Success)
            {
                var table = response.Value.Table;
                var columns = table.Columns.Select(c => c.Name).ToList();

                foreach (var row in table.Rows)
                {
                    var opId = GetValue<string>(row, columns, "operation_Id");
                    if (!string.IsNullOrEmpty(opId) && results.TryGetValue(opId, out var result))
                    {
                        result.Exceptions.Add(new ExceptionInfo
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
        }
        catch
        {
            // Query failed, continue with empty results
        }
    }

    private async Task QueryTracesBatchAsync(string operationIdList,
                                             QueryTimeRange timeRange,
                                             Dictionary<string, DiagnosticResult> results,
                                             CancellationToken cancellationToken)
    {
        var query = $"""

                     traces
                     | where operation_Id in ('{operationIdList}')
                     | where severityLevel >= 2
                     | order by timestamp desc
                     | project operation_Id, timestamp, message, severityLevel

                     """;

        try
        {
            var response = await _logsClient!.QueryResourceAsync(_resourceId,
                                                                 query,
                                                                 timeRange,
                                                                 cancellationToken: cancellationToken);

            if (response.Value.Status == LogsQueryResultStatus.Success)
            {
                var table = response.Value.Table;
                var columns = table.Columns.Select(c => c.Name).ToList();

                foreach (var row in table.Rows)
                {
                    var opId = GetValue<string>(row, columns, "operation_Id");
                    if (!string.IsNullOrEmpty(opId) && results.TryGetValue(opId, out var result))
                    {
                        result.Traces.Add(new TraceInfo
                        {
                            Timestamp = GetValue<DateTimeOffset>(row, columns, "timestamp"),
                            Message = GetValue<string>(row, columns, "message"),
                            SeverityLevel = GetValue<int>(row, columns, "severityLevel")
                        });
                    }
                }
            }
        }
        catch
        {
            // Query failed, continue with empty results
        }
    }

    private async Task QueryDependenciesBatchAsync(string operationIdList,
                                                   QueryTimeRange timeRange,
                                                   Dictionary<string, DiagnosticResult> results,
                                                   CancellationToken cancellationToken)
    {
        var query = $"""

                     dependencies
                     | where operation_Id in ('{operationIdList}')
                     | where success == false
                     | order by timestamp desc
                     | project operation_Id, timestamp, type, target, name, data, resultCode, success, duration

                     """;

        try
        {
            var response = await _logsClient!.QueryResourceAsync(_resourceId,
                                                                 query,
                                                                 timeRange,
                                                                 cancellationToken: cancellationToken);

            if (response.Value.Status == LogsQueryResultStatus.Success)
            {
                var table = response.Value.Table;
                var columns = table.Columns.Select(c => c.Name).ToList();

                foreach (var row in table.Rows)
                {
                    var opId = GetValue<string>(row, columns, "operation_Id");
                    if (!string.IsNullOrEmpty(opId) && results.TryGetValue(opId, out var result))
                    {
                        result.FailedDependencies.Add(new DependencyInfo
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
        }
        catch
        {
            // Query failed, continue with empty results
        }
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
