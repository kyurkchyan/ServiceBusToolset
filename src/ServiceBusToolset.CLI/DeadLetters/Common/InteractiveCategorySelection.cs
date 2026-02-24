using Azure.Messaging.ServiceBus;

namespace ServiceBusToolset.CLI.DeadLetters.Common;

public sealed record InteractiveCategorySelection(IReadOnlyList<ServiceBusReceivedMessage> Messages,
                                                  int SelectedCategoryCount);
