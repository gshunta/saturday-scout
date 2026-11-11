namespace SaturdayScout.Player;

public sealed class PlayerConnectionState
{
    public bool IsConnected { get; private set; } = true;
    public event Action? Changed;

    public void SetConnected(bool connected)
    {
        if (IsConnected == connected) return;
        IsConnected = connected;
        Changed?.Invoke();
    }
}
