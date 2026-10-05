// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net.Sockets;
using Renci.SshNet.Common;

namespace FileFluss.Core.Providers.Sftp;

/// <summary>
/// SSH/SFTP failures → <see cref="CloudProviderException"/>. A port of the macOS
/// <c>mapSFTPFailure</c>, which pattern-matched OpenSSH's stderr; here the library's exception
/// types carry the same distinctions (which credential to fix, key format vs passphrase, host
/// key, refused/timed-out/unresolvable, missing path, server refusal).
/// </summary>
internal static class SftpErrors
{
    public static CloudProviderException Map(Exception error, string? path, SftpAuthMethod method, SftpHostKeyCheck? hostKey = null)
    {
        var e = error is AggregateException { InnerExceptions.Count: 1 } aggregate ? aggregate.InnerExceptions[0] : error;
        if (e is CloudProviderException provider)
        {
            return provider;
        }

        // The host key changed: SSH.NET only reports a failed key exchange, the check knows why.
        if (hostKey?.Mismatch == true)
        {
            return HostKeyChanged();
        }

        switch (e)
        {
            case SftpKeyLoadException key:
                if (key.InnerException is SshPassPhraseNullOrEmptyException)
                {
                    return CloudProviderException.CommandFailed(Localization.L("The private key is protected by a passphrase. Enter it and try again."));
                }

                return key.PassphraseProblem
                    ? CloudProviderException.CommandFailed(Localization.L("Wrong passphrase for the private key."))
                    : CloudProviderException.CommandFailed(Localization.L("Private key is in an unsupported format. Convert it to OpenSSH format (e.g. with `puttygen key.ppk -O private-openssh -o id_rsa`)."));

            case SshAuthenticationException auth:
                // Distinguish password vs key so the user knows what to fix. A server that only
                // takes keys answers a password login with "(publickey)" too, which is the hint
                // they need.
                if (auth.Message.Contains("(publickey)", StringComparison.OrdinalIgnoreCase))
                {
                    return CloudProviderException.CommandFailed(Localization.L("SSH key was rejected by the server. Make sure the public key is in the server's ~/.ssh/authorized_keys (or added to the provider's SSH-keys panel for managed services like Hetzner Storage Box)."));
                }

                return method == SftpAuthMethod.Password
                    ? CloudProviderException.CommandFailed(Localization.L("SSH authentication failed. Check the username and password."))
                    : CloudProviderException.CommandFailed(Localization.L("SSH authentication failed. The server may not have your public key authorized, or the passphrase is wrong."));

            case SocketException socket:
                return MapSocket(socket);

            case SshOperationTimeoutException:
                return CloudProviderException.CommandFailed(Localization.L("Connection timed out. Check the hostname, port, and your network."));

            case SftpPathNotFoundException:
                return CloudProviderException.NotFound(path ?? e.Message);

            case SftpPermissionDeniedException:
                return CloudProviderException.CommandFailed(Localization.L("Permission denied: {0}", path ?? string.Empty));

            case SftpException sftp:
                return CloudProviderException.CommandFailed(string.IsNullOrWhiteSpace(sftp.Message)
                    ? Localization.L("SFTP command failed.")
                    : path is null ? sftp.Message : $"{sftp.Message}: {path}");

            case SshConnectionException or ProxyException or ObjectDisposedException:
                return CloudProviderException.Network(e);

            case IOException { InnerException: SocketException socket }:
                return MapSocket(socket);

            default:
                return CloudProviderException.CommandFailed(string.IsNullOrWhiteSpace(e.Message) ? Localization.L("SFTP command failed.") : e.Message);
        }
    }

    public static CloudProviderException HostKeyChanged()
        => CloudProviderException.CommandFailed(Localization.L("Host key verification failed — the server's host key changed. If you expected this, edit the account and connect again to accept the new key."));

    /// <summary>The session is gone (dropped, timed out, closed): discard it and reconnect.</summary>
    public static bool IsConnectionLost(Exception e)
        => e is SshConnectionException or ObjectDisposedException or SocketException or SshOperationTimeoutException or ProxyException ||
           e is IOException { InnerException: SocketException };

    private static CloudProviderException MapSocket(SocketException socket) => socket.SocketErrorCode switch
    {
        SocketError.ConnectionRefused =>
            CloudProviderException.CommandFailed(Localization.L("Connection refused — is the SSH daemon running on this host and port?")),
        SocketError.TimedOut =>
            CloudProviderException.CommandFailed(Localization.L("Connection timed out. Check the hostname, port, and your network.")),
        SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain =>
            CloudProviderException.CommandFailed(Localization.L("Could not resolve hostname. Check spelling or DNS.")),
        SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.NetworkDown =>
            CloudProviderException.CommandFailed(Localization.L("Network is unreachable from this PC.")),
        _ => CloudProviderException.Network(socket),
    };
}
