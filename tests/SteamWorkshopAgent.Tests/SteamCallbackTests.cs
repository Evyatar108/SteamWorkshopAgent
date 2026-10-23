using System.Runtime.InteropServices;
using SteamWorkshopAgent;

namespace SteamWorkshopAgent.Tests;

public sealed class SteamCallbackTests
{
    [Fact]
    public void Windows_Create_Result_Aligns_PublishedFileId_To_Eight_Bytes()
    {
        Assert.Equal(8, Marshal.OffsetOf<SteamCreateItemResult>("PublishedFileId").ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<SteamCreateItemResult>("UserNeedsToAcceptWorkshopLegalAgreement").ToInt32());
        Assert.Equal(24, Marshal.SizeOf<SteamCreateItemResult>());
    }

    [Fact]
    public void Small_Create_Result_Aligns_PublishedFileId_To_Four_Bytes()
    {
        Assert.Equal(4, Marshal.OffsetOf<SteamCreateItemResultSmall>("PublishedFileId").ToInt32());
        Assert.Equal(12, Marshal.OffsetOf<SteamCreateItemResultSmall>("UserNeedsToAcceptWorkshopLegalAgreement").ToInt32());
        Assert.Equal(16, Marshal.SizeOf<SteamCreateItemResultSmall>());
    }

    [Theory]
    [InlineData(true, 24, 8, 16)]
    [InlineData(false, 16, 4, 12)]
    public void Create_Result_Decodes_Platform_Packed_Bytes(bool windows, int size, int idOffset, int legalOffset)
    {
        const long itemId = 0x0123456789abcdef;
        Assert.Equal(size, SteamCallbackPacking.CreateResultSize(windows));
        var bytes = new byte[size];
        BitConverter.GetBytes(1).CopyTo(bytes, 0);
        BitConverter.GetBytes(itemId).CopyTo(bytes, idOffset);
        bytes[legalOffset] = 1;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(bytes, 0, buffer, size);
            var result = SteamCallbackPacking.ReadCreateResult(buffer, windows);
            Assert.Equal(1, result.Result);
            Assert.Equal((ulong)itemId, result.PublishedFileId);
            Assert.True(result.UserNeedsToAcceptWorkshopLegalAgreement);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Submit_Result_Uses_Correct_Offsets_For_Both_Packings(bool windows)
    {
        var type = windows ? typeof(SteamSubmitItemUpdateResult) : typeof(SteamSubmitItemUpdateResultSmall);
        Assert.Equal(4, Marshal.OffsetOf(type, "UserNeedsToAcceptWorkshopLegalAgreement").ToInt32());
        Assert.Equal(8, Marshal.OffsetOf(type, "PublishedFileId").ToInt32());
        Assert.Equal(16, SteamCallbackPacking.SubmitResultSize(windows));
        var buffer = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.Copy(new byte[16], 0, buffer, 16);
            Marshal.WriteInt32(buffer, 1);
            Marshal.WriteByte(buffer, 4, 1);
            Marshal.WriteInt64(buffer, 8, 123456789);
            var result = SteamCallbackPacking.ReadSubmitResult(buffer, windows);
            Assert.Equal(1, result.Result);
            Assert.True(result.UserNeedsToAcceptWorkshopLegalAgreement);
            Assert.Equal(123456789UL, result.PublishedFileId);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
