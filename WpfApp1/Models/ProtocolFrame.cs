namespace WpfApp1.Models;

public sealed class ProtocolFrame
{
    public ProtocolFrame(byte command, byte subCommand, byte[] data)
    {
        Command = command;
        SubCommand = subCommand;
        Data = data ?? throw new ArgumentNullException(nameof(data));
    }

    public byte Command { get; }

    public byte SubCommand { get; }

    public byte[] Data { get; }
}
