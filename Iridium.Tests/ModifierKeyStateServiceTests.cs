using Iridium.Client.Core;

namespace Iridium.Tests;

public sealed class ModifierKeyStateServiceTests
{
    [Fact]
    public void ShiftStateOnlyNotifiesWhenItsValueChanges()
    {
        var service = new ModifierKeyStateService();
        var changes = 0;
        service.Changed += () => changes++;

        service.SetShift(true);
        service.SetShift(true);
        service.SetShift(false);

        Assert.False(service.ShiftPressed);
        Assert.Equal(2, changes);
    }
}
