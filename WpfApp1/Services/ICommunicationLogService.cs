using System.Collections.ObjectModel;
using WpfApp1.Models;

namespace WpfApp1.Services;

public interface ICommunicationLogService
{
    ObservableCollection<CommunicationLogEntry> Entries { get; }

    int RetentionLimit { get; }

    void RecordSent(ReadOnlySpan<byte> data, LinFrameDefinition? definition = null);

    void RecordReceived(ReadOnlySpan<byte> data, LinFrameDefinition? definition = null, string? error = null);

    void SetRetentionLimit(int retentionLimit);

    void Clear();
}
