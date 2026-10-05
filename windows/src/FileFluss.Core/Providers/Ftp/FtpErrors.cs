// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net.Sockets;
using System.Security.Authentication;
using FluentFTP.Exceptions;

namespace FileFluss.Core.Providers.Ftp;

/// <summary>
/// FluentFTP failures → <see cref="CloudProviderException"/>, following the macOS mapping of
/// curl's FTP exit codes (login denied, resolve/connect/timeout, access denied, not found,
/// TLS) with the server's own reply text wherever there is one.
/// </summary>
internal static class FtpErrors
{
    public static CloudProviderException Map(Exception error, string? path)
    {
        var e = Unwrap(error);
        switch (e)
        {
            case CloudProviderException provider:
                return provider;

            case FtpAuthenticationException auth:
                // "530 Non-anonymous sessions must use encryption" is a login refusal too, but
                // the fix is the FTPS switch, not the password.
                return MentionsTls(ReplyText(auth.Message))
                    ? CloudProviderException.CommandFailed(ReplyText(auth.Message))
                    : CloudProviderException.InvalidCredentials();

            case FtpInvalidCertificateException or AuthenticationException:
                return CloudProviderException.CommandFailed(Localization.L(
                    "TLS/SSL error: {0}",
                    string.IsNullOrWhiteSpace(e.Message) ? Localization.L("the server's certificate could not be verified.") : e.Message));

            case FtpSecurityNotAvailableException:
                return CloudProviderException.CommandFailed(Localization.L("TLS/SSL error: {0}", e.Message));

            case FtpMissingObjectException:
                return CloudProviderException.NotFound(path ?? string.Empty);

            case FtpCommandException command:
                return MapReply(command.CompletionCode, ReplyText(command.Message), path);

            case TimeoutException:
                return CloudProviderException.CommandFailed(Localization.L("Connection timed out."));

            case SocketException socket:
                return MapSocket(socket);

            case IOException { InnerException: SocketException socket }:
                return MapSocket(socket);

            case IOException or FtpMissingSocketException or FtpException or ObjectDisposedException:
                return CloudProviderException.Network(e);

            default:
                return CloudProviderException.CommandFailed(string.IsNullOrWhiteSpace(e.Message)
                    ? Localization.L("FTP command failed.")
                    : e.Message);
        }
    }

    /// <summary>
    /// A server reply. 550 is both "no such file" and "permission denied", told apart by the
    /// text; 452/552 are out-of-space replies; 530 "not logged in".
    /// </summary>
    internal static CloudProviderException MapReply(string? code, string? message, string? path)
    {
        var text = string.IsNullOrWhiteSpace(message) ? Localization.L("FTP command failed.") : message.Trim();
        var lower = text.ToLowerInvariant();
        switch (code)
        {
            case "550" when lower.Contains("no such file", StringComparison.Ordinal) ||
                            lower.Contains("not found", StringComparison.Ordinal) ||
                            lower.Contains("does not exist", StringComparison.Ordinal) ||
                            lower.Contains("cannot find", StringComparison.Ordinal) ||
                            lower.Contains("can't find", StringComparison.Ordinal) ||
                            lower.Contains("not exist", StringComparison.Ordinal):
                return CloudProviderException.NotFound(path ?? text);
            case "452" or "552":
                return CloudProviderException.QuotaExceeded();
            case "530" when !MentionsTls(text):
                return CloudProviderException.InvalidCredentials();
            default:
                return CloudProviderException.CommandFailed(text);
        }
    }

    /// <summary>FluentFTP's exception text is "Code: 550 Message: …"; the user wants the server's words.</summary>
    internal static string ReplyText(string message)
    {
        const string Marker = " Message: ";
        var index = message.IndexOf(Marker, StringComparison.Ordinal);
        return message.StartsWith("Code: ", StringComparison.Ordinal) && index >= 0 ? message[(index + Marker.Length)..] : message;
    }

    /// <summary>
    /// Failures after which the control connection can't be trusted for the next command. A
    /// server reply (550 and friends) leaves it perfectly usable.
    /// </summary>
    public static bool IsConnectionLevel(Exception error)
        => Unwrap(error) is not (FtpCommandException or FtpMissingObjectException or CloudProviderException);

    private static CloudProviderException MapSocket(SocketException socket) => socket.SocketErrorCode switch
    {
        SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain =>
            CloudProviderException.CommandFailed(Localization.L("Could not resolve host. Check the server address.")),
        SocketError.TimedOut =>
            CloudProviderException.CommandFailed(Localization.L("Connection timed out.")),
        _ => CloudProviderException.CommandFailed(Localization.L("Could not connect — check the host, port, and that FTP is reachable.")),
    };

    private static bool MentionsTls(string message)
        => message.Contains("TLS", StringComparison.OrdinalIgnoreCase) ||
           message.Contains("SSL", StringComparison.OrdinalIgnoreCase) ||
           message.Contains("encrypt", StringComparison.OrdinalIgnoreCase);

    private static Exception Unwrap(Exception e)
        => e is AggregateException { InnerExceptions.Count: 1 } aggregate ? Unwrap(aggregate.InnerExceptions[0]) : e;
}
