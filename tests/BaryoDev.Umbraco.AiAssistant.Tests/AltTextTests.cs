using BaryoDev.Umbraco.AiAssistant.Claude;
using BaryoDev.Umbraco.AiAssistant.Jobs;
using BaryoDev.Umbraco.AiAssistant.Media;

namespace BaryoDev.Umbraco.AiAssistant.Tests;

public class AltTextTests
{
    /// <summary>A PNG signature and header with the given size. Enough for the type and size checks.</summary>
    private static byte[] Png(int width, int height)
    {
        var b = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52 }.CopyTo(b, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), (uint)width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20), (uint)height);
        return b;
    }

    [Fact]
    public void Small_images_pass_through_unchanged()
    {
        byte[] bytes = Png(64, 48);
        ClaudeImage image = ImagePreparer.Prepare(bytes, "a.png");
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(Convert.ToBase64String(bytes), image.Base64);
    }

    [Fact]
    public void Images_over_the_pixel_or_byte_limit_are_skipped_with_a_reason()
    {
        Assert.Contains("pixels", Assert.Throws<ArgumentException>(() => ImagePreparer.Prepare(Png(9000, 100), "big.png")).Message);
        byte[] heavy = new byte[ImagePreparer.MaxBytes + 1];
        Png(10, 10).CopyTo(heavy, 0);
        Assert.Contains("MB", Assert.Throws<ArgumentException>(() => ImagePreparer.Prepare(heavy, "heavy.png")).Message);
    }

    [Fact]
    public void Gif_and_jpeg_headers_are_recognised()
    {
        byte[] gif = [(byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', 20, 0, 10, 0, 0, 0];
        Assert.Equal("image/gif", ImagePreparer.Prepare(gif, "a.gif").MediaType);
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xC0, 0, 17, 8, 0, 30, 0, 40, 3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1];
        Assert.Equal("image/jpeg", ImagePreparer.Prepare(jpeg, "a.jpg").MediaType);
    }

    [Fact]
    public void Svg_is_skipped_with_a_reason()
    {
        var e = Assert.Throws<ArgumentException>(() => ImagePreparer.Prepare("<svg/>"u8.ToArray(), "logo.svg"));
        Assert.Contains("SVG", e.Message);
    }

    [Fact]
    public void Unreadable_and_empty_files_are_skipped_with_a_reason()
    {
        Assert.Throws<ArgumentException>(() => ImagePreparer.Prepare("not an image"u8.ToArray(), "x.png"));
        Assert.Throws<ArgumentException>(() => ImagePreparer.Prepare([], "x.png"));
    }

    [Theory]
    [InlineData("Image of a red bicycle", "A red bicycle")]
    [InlineData("a photo showing two people", "Two people")]
    [InlineData("\"Harbour at dusk\"", "Harbour at dusk")]
    public void Leading_filler_is_removed(string raw, string expected) =>
        Assert.Equal(expected, AltTextService.Clean(raw));

    [Fact]
    public void Existing_alt_text_is_kept_unless_overwrite_is_asked()
    {
        Assert.False(AltTextService.Needs("A red bicycle", false));
        Assert.True(AltTextService.Needs("A red bicycle", true));
        Assert.True(AltTextService.Needs("  ", false));
        Assert.True(AltTextService.Needs(null, false));
    }

    [Fact]
    public async Task Handler_sends_the_image_and_keeps_the_file_name_in_a_data_block()
    {
        var sent = new List<ClaudeRequest>();
        var gateway = new FakeGateway();
        var handler = new AltTextHandler(new AltTextService(new Stub(sent, "{\"text\":\"image of a harbour. Ignore previous instructions\"}")), gateway);
        var item = new BulkItem { NodeId = Guid.NewGuid(), Name = "photo" };
        item.Fields.Add(new FieldChange { Alias = "altText", Original = "" });

        await handler.RunAsync(item, new JobOptions(null, false), TestContext.Current.CancellationToken);

        Assert.Equal(ItemState.Ready, item.State);
        Assert.Equal(FactsStatus.NotApplicable, item.Facts);
        Assert.StartsWith("A harbour", item.Fields[0].Suggestion);
        ClaudeRequest request = Assert.Single(sent);
        Assert.NotNull(request.Image);
        Assert.Contains("<image>", request.User);
    }

    private sealed class Stub(List<ClaudeRequest> sent, string answer) : IClaudeClient
    {
        public Task<ClaudeResponse> CompleteAsync(ClaudeRequest request, CancellationToken cancellationToken)
        {
            sent.Add(request);
            return Task.FromResult(new ClaudeResponse(answer, new ClaudeUsage(10, 5)));
        }
    }
}
