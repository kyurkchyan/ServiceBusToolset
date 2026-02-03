using System.Text.RegularExpressions;

namespace ServiceBusToolset.Application.Common.ServiceBus.Helpers;

public static class WildcardFilterHelper
{
    public static Func<string, bool> CreateFilterPredicate(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return _ => true;
        }

        if (filter.Contains('*') || filter.Contains('?'))
        {
            var regexPattern = "^" + Regex.Escape(filter)
                                          .Replace("\\*", ".*")
                                          .Replace("\\?", ".") + "$";
            var regex = new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
            return name => regex.IsMatch(name);
        }

        return name => name.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }
}
