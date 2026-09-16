using System.Collections.ObjectModel;
using WpfApp1.Models;

namespace WpfApp1.Services;

public interface ICommunicationLogService
{
    ObservableCollection<CommunicationLogEntry> Entries { get; }

    void RecordSent(ReadOnlySpan<byte> data);

    void RecordReceived(ReadOnlySpan<byte> data);

    void Clear();
}
