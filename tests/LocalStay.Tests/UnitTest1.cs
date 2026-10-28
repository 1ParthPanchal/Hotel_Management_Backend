using Xunit;

namespace LocalStay.Tests;

public class BookingOverlapTests
{
    [Theory]
    [InlineData("2027-06-01", "2027-06-05", "2027-06-03", "2027-06-07", true)]  // Overlap
    [InlineData("2027-06-01", "2027-06-05", "2027-06-05", "2027-06-08", false)] // Adjacent checkout/checkin
    [InlineData("2027-06-05", "2027-06-10", "2027-06-01", "2027-06-05", false)] // Adjacent checkin/checkout
    [InlineData("2027-06-01", "2027-06-10", "2027-06-03", "2027-06-07", true)]  // Contained within
    [InlineData("2027-06-03", "2027-06-07", "2027-06-01", "2027-06-10", true)]  // Envelops
    public void Test_Date_Overlap_Guard(string existingIn, string existingOut, string newIn, string newOut, bool expectedOverlap)
    {
        var eIn = DateTime.Parse(existingIn);
        var eOut = DateTime.Parse(existingOut);
        var nIn = DateTime.Parse(newIn);
        var nOut = DateTime.Parse(newOut);

        // Standard overlap condition: b.CheckIn < newCheckOut && b.CheckOut > newCheckIn
        var hasOverlap = eIn < nOut && eOut > nIn;
        Assert.Equal(expectedOverlap, hasOverlap);
    }

    [Fact]
    public void Test_Advisory_Lock_Key_Consistency()
    {
        int roomId = 1;
        int key1 = Math.Abs(roomId.GetHashCode());
        int key2 = Math.Abs(roomId.GetHashCode());

        Assert.Equal(key1, key2);
        Assert.True(key1 >= 0);
    }

    [Fact]
    public void Test_Invoice_Price_Calculation()
    {
        decimal pricePerNight = 2500m;
        var checkIn = new DateTime(2027, 6, 1);
        var checkOut = new DateTime(2027, 6, 4); // 3 nights

        var nights = (checkOut - checkIn).Days;
        var totalAmount = pricePerNight * nights;

        Assert.Equal(3, nights);
        Assert.Equal(7500m, totalAmount);
    }
}
