using ProoveGaming.Infrastructure.Classes;

namespace ProoveGaming.Infrastructure.Tests;

// The InTheHand Windows BluetoothDevice.Id → ulong seam the advertisement watcher matches on.
// The parser must be strict: anything that is not exactly 12 hex digits is rejected, because a
// false positive here would put a wrong address into the matcher snapshot and silently kill a
// device's reconnect path. See docs/plans/fix-devicehost-ble-memory-leak/.
public class BluetoothAddressParserTests
{
	[Theory]
	[InlineData("E0F1A2B3C4D5", 0xE0F1A2B3C4D5UL)]
	[InlineData("e0f1a2b3c4d5", 0xE0F1A2B3C4D5UL)]
	[InlineData("000000000000", 0UL)]
	[InlineData("FFFFFFFFFFFF", 0xFFFFFFFFFFFFUL)]
	[InlineData("00000000000f", 0xFUL)]
	public void TryParse_TwelveHexDigits_RoundTripsToExpectedAddress(string deviceId, ulong expected)
	{
		var parsed = BluetoothAddressParser.TryParse(deviceId, out var address);

		Assert.True(parsed);
		Assert.Equal(expected, address);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("E0F1A2B3C4D")] // 11 chars — too short
	[InlineData("E0F1A2B3C4D55")] // 13 chars — too long
	[InlineData("E0F1A2B3C4G5")] // non-hex character
	[InlineData("E0:F1:A2:B3:C4:D5")] // separators — not the InTheHand Windows Id shape
	[InlineData(" 0F1A2B3C4D5")] // right length, but padded — must not smuggle a shorter address
	[InlineData("E0F1A2B3C4D ")] // trailing whitespace
	[InlineData("            ")] // all whitespace at the right length
	public void TryParse_InvalidInput_IsRejectedWithZeroAddress(string? deviceId)
	{
		var parsed = BluetoothAddressParser.TryParse(deviceId, out var address);

		Assert.False(parsed);
		Assert.Equal(0UL, address);
	}
}
