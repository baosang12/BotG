namespace cAlgo.API
{
    // Minimal enum shim only if the real package does not define ErrorCode.NoError in build context.
    public enum ErrorCode
    {
        NoError = 0,
        // Other values left unspecified
        Unknown = 1
    }
}
