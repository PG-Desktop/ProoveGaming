using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.Services;

namespace ProoveGaming.Infrastructure.Tests;

// The transport → table-chunk-size mapping both the key-mapping and macro factories resolve a
// Smart Infinite service with. Getting this wrong is silent on the wire: a 56-byte-chunked
// transfer over the 2.4G receiver is acked packet by packet and then discarded (or read back
// misaligned), so the only guard outside a hardware run is pinning the mapping here.
public class DeviceKeyMappingServiceFactoryTests
{
	private static DeviceMap Row(string? connectionType) => new()
	{
		Name = "RAMPAGE",
		VendorId = "1389",
		ProductId = "49271",
		ConnectionType = connectionType,
	};

	[Theory]
	[InlineData("Dongle2_4G")]
	[InlineData("dongle2_4g")]
	public void TablePayloadFor_ReceiverRow_UsesThe24ByteChunking(string connectionType)
	{
		Assert.Equal(
			SmartInfiniteProtocol.Receiver24GPayloadLength,
			DeviceKeyMappingServiceFactory.TablePayloadFor(Row(connectionType)));
	}

	[Theory]
	[InlineData("Wired")]
	[InlineData(null)]
	public void TablePayloadFor_AnyOtherRow_KeepsTheWiredChunking(string? connectionType)
	{
		Assert.Equal(
			SmartInfiniteProtocol.MaxPayloadLength,
			DeviceKeyMappingServiceFactory.TablePayloadFor(Row(connectionType)));
	}
}
