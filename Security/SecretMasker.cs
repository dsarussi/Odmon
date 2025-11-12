namespace Odmon.Worker.Security;

public static class SecretMasker
{
    public static string Mask(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return string.Empty;
        }

        if (secret.Length <= 4)
        {
            return new string('*', secret.Length);
        }

        var visible = 4;
        return $"{new string('*', secret.Length - visible)}{secret[^visible..]}";
    }
}

