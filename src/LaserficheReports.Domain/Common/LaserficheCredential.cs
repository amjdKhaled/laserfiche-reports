namespace LaserficheReports.Domain.Common;

/// <summary>
/// Holds a Laserfiche username and password pair retrieved from a secure credential store.
/// Credentials are never serialised, logged, or stored in any configuration file as plain text.
/// </summary>
/// <param name="Username">Laserfiche username.</param>
/// <param name="Password">Plain-text password retrieved from the secure store at runtime only.</param>
public sealed record LaserficheCredential(string Username, string Password)
{
    /// <summary>
    /// Overridden to prevent accidental credential exposure in log output.
    /// Always returns the literal string <c>[REDACTED]</c>.
    /// </summary>
    public override string ToString() => "[REDACTED]";
}
