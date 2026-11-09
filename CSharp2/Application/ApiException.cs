namespace CSharp2.Application;

public sealed class ApiException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public static class Input
{
    public static string Required(string? value, int max = 200)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > max)
            throw new ApiException(400, $"A value between 1 and {max} characters is required.");
        return value;
    }

    public static string Code(string? value, int max = 50) => Required(value, max).ToUpperInvariant();

    public static decimal Quantity(decimal value)
    {
        if (value < 0 || value > 999999999999999.999m || decimal.Round(value, 3) != value)
            throw new ApiException(400, "Quantity must be nonnegative and fit decimal(18,3).");
        return value;
    }

    public static decimal Money(decimal value)
    {
        if (value < 0 || value > 9999999999999999.99m || decimal.Round(value, 2) != value)
            throw new ApiException(400, "Money must be nonnegative and fit decimal(18,2).");
        return value;
    }

    public static void ValidatePage(int page, int pageSize)
    {
        if (page < 1 || page > 1_000_000 || pageSize is < 1 or > 100)
            throw new ApiException(400, "Page must be 1–1000000 and pageSize must be 1–100.");
    }
}
