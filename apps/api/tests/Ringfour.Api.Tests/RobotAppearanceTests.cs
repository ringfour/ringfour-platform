using Ringfour.Api.Errors;
using Ringfour.Api.Robots;

namespace Ringfour.Api.Tests;

public sealed class RobotAppearanceTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("bot")]
    [InlineData("rover")]
    [InlineData("arm")]
    [InlineData("drone")]
    [InlineData("base101")]
    [InlineData("data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a4WQAAAAASUVORK5CYII=")]
    public void Accepts_supported_choices(string? value) => RobotAppearance.Validate(value);

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("https://example.com/robot.png")]
    [InlineData("data:image/svg+xml;base64,PHN2Zy8+")]
    [InlineData("data:image/png;base64,broken")]
    [InlineData("data:image/png;base64,aGVsbG8=")]
    [InlineData("data:image/jpeg;base64,iVBORw0KGgo=")]
    [InlineData("data:image/png;base64,iVBORw0KGgo=\n")]
    public void Rejects_unsupported_or_mismatched_images(string value) =>
        Assert.Throws<ApiException>(() => RobotAppearance.Validate(value));

    [Fact]
    public void Rejects_oversized_image()
    {
        var bytes = new byte[RobotAppearance.MaxImageBytes + 1];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0);
        Assert.Throws<ApiException>(() => RobotAppearance.Validate("data:image/png;base64," + Convert.ToBase64String(bytes)));
    }
}
