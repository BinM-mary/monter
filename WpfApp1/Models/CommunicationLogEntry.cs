namespace WpfApp1.Models;

public enum CommunicationLogDirection
{
    Sent,
    Received
}

public sealed record CommunicationLogEntry(
    DateTimeOffset Timestamp,
    CommunicationLogDirection Direction,
    string Data)
{
    public string TimestampText => Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");

    public string DirectionText => Direction == CommunicationLogDirection.Sent ? "TX" : "RX";
}
