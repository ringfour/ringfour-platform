using Ringfour.Api.Errors;

namespace Ringfour.Api.Robots;

public static class RobotAppearance
{
    public const int MaxImageBytes = 256 * 1024;

    public static void Validate(string? value)
    {
        if (value is null or "bot" or "rover" or "arm" or "drone" or "base101") return;
        if (value.Length > 350000) throw ApiException.InvalidInput("Robot images must be at most 256 KiB.");
        var comma = value.IndexOf(',');
        if (comma < 0) throw ApiException.InvalidInput("Choose a robot icon or a PNG, JPEG or WebP image.");
        var prefix = value[..comma];
        if (prefix is not ("data:image/png;base64" or "data:image/jpeg;base64" or "data:image/webp;base64"))
            throw ApiException.InvalidInput("Only PNG, JPEG and WebP images are supported.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(value[(comma + 1)..]); }
        catch (FormatException) { throw ApiException.InvalidInput("Robot image is not valid base64."); }
        if (bytes.Length > MaxImageBytes || Convert.ToBase64String(bytes) != value[(comma + 1)..])
            throw ApiException.InvalidInput("Robot image must be canonical base64 and at most 256 KiB.");
        var valid = prefix switch
        {
            "data:image/png;base64" => bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            "data:image/jpeg;base64" => bytes.AsSpan().StartsWith(new byte[] { 255, 216, 255 }),
            _ => bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8),
        };
        if (!valid) throw ApiException.InvalidInput("Robot image content does not match its image type.");
    }
}
