using AltMixer.Core.Model;
using AltMixer.Core.State;

namespace AltMixer.Core.Tests;

public class DeviceOrderingTests
{
    static DeviceInfo D(string id, bool isDefault = false, DeviceStatus status = DeviceStatus.Active) =>
        new() { Id = id, Name = id, Flow = Flow.Render, Status = status, Fingerprint = id, IsDefault = isDefault };

    static string[] Ids(IEnumerable<DeviceInfo> d) => d.Select(x => x.Id).ToArray();

    [Fact]
    public void Without_an_order_defaults_come_first_then_name_then_disabled()
    {
        var sorted = DeviceOrdering.Sort([D("c"), D("off", status: DeviceStatus.Disabled), D("b"), D("z", isDefault: true)], []);
        Assert.Equal(["z", "b", "c", "off"], Ids(sorted));
    }

    [Fact]
    public void Ordered_devices_come_first_in_that_order()
    {
        var sorted = DeviceOrdering.Sort([D("a"), D("b"), D("c"), D("z", isDefault: true)], ["c", "a"]);
        Assert.Equal(["c", "a", "z", "b"], Ids(sorted));
    }

    [Fact]
    public void Move_swaps_with_neighbour_and_stores_whole_section()
    {
        var order = DeviceOrdering.Move([], ["a", "b", "c"], "c", -1);
        Assert.Equal(["a", "c", "b"], order);
    }

    [Fact]
    public void MoveTo_drags_across_several_places()
    {
        Assert.Equal(["d", "a", "b", "c"], DeviceOrdering.MoveTo([], ["a", "b", "c", "d"], "d", 0));
        Assert.Equal(["b", "c", "a", "d"], DeviceOrdering.MoveTo([], ["a", "b", "c", "d"], "a", 2));
        Assert.Null(DeviceOrdering.MoveTo([], ["a", "b"], "a", 0));
    }

    [Fact]
    public void Move_past_either_end_does_nothing()
    {
        Assert.Null(DeviceOrdering.Move([], ["a", "b"], "a", -1));
        Assert.Null(DeviceOrdering.Move([], ["a", "b"], "b", +1));
    }

    [Fact]
    public void Disconnected_devices_keep_their_slot()
    {
        // "x" is unplugged: it isn't shown, but should come back between a and b.
        var order = DeviceOrdering.Move(["a", "x", "b", "c"], ["a", "b", "c"], "c", -1)!;
        Assert.Equal(["a", "x", "c", "b"], order);
        Assert.Equal(["a", "x", "c", "b"], Ids(DeviceOrdering.Sort([D("b"), D("c"), D("x"), D("a")], order)));
    }

    [Fact]
    public void Adopt_moves_the_order_slot_to_the_new_id()
    {
        var store = new DesiredStore(null);
        store.SetDeviceOrder(["a", "old", "b"]);
        store.Adopt("old", "new");
        Assert.Equal(["a", "new", "b"], store.Data.Ui.DeviceOrder);
    }
}
