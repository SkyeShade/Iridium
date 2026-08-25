namespace Iridium.Client.Core;

public sealed class ModifierKeyStateService
{
    public bool ShiftPressed { get; private set; }
    public event Action? Changed;

    public void SetShift(bool pressed)
    {
        if (ShiftPressed == pressed) return;
        ShiftPressed = pressed;
        Changed?.Invoke();
    }
}
