namespace KDEConnect.WP81.Core
{
    /// <summary>
    /// Identifies the running build, on screen and in the startup log.
    /// </summary>
    /// <remarks>
    /// Deploying a Windows Phone package takes minutes and it is easy to test
    /// last night's build by mistake. That happened twice here: a fix was
    /// reported as "still failing" before it could possibly have been deployed.
    /// A stamp that the operator can read off the Diagnostics page ends the
    /// question, and it costs one string.
    ///
    /// Bump this in the same commit as any behavioural change.
    /// </remarks>
    public static class BuildStamp
    {
        public const string Value = "0.5.1+datawriter";
    }
}
