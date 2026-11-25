using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace AxisSdReader.Core.Agent;

/// <summary>
/// The named pipe between the (elevated) app and the (unelevated) MCP bridge: one length-prefixed JSON
/// request and one reply per connection.
/// <para>Security: the pipe name is per user; the server is created as the first and only instance (so an
/// impostor that grabbed the name first makes the app refuse to start agent access rather than share it);
/// its ACL admits only the interactive user's own account, never network logons; requests and replies are
/// size-capped. The bridge checks the pipe is owned by Administrators — i.e. created by the elevated app, not
/// by an unelevated program squatting on the name.</para>
/// </summary>
public static class AgentPipe
{
    public const int MaxRequestBytes = 64 * 1024;
    public const int MaxResponseBytes = 16 * 1024 * 1024;

    /// <summary>The pipe name for the current user.</summary>
    public static string Name
    {
        get
        {
            var sid = OperatingSystem.IsWindows() ? CurrentUserSid() : Environment.UserName;
            return $"AxisSdReader.Agent.{sid}";
        }
    }

    [SupportedOSPlatform("windows")]
    private static string CurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? identity.Name;
    }

    /// <summary>Creates the app's pipe endpoint. Throws <see cref="IOException"/> if another process owns the name.</summary>
    [SupportedOSPlatform("windows")]
    public static NamedPipeServerStream CreateServer()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("No user SID.");
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));

        // maxNumberOfServerInstances: 1 → FILE_FLAG_FIRST_PIPE_INSTANCE: fails if the name already exists.
        return NamedPipeServerStreamAcl.Create(Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 0, 0, security);
    }

    /// <summary>
    /// Whether the connected server end was created by an elevated process (its owner is Administrators).
    /// A program running unelevated can't create a pipe owned by Administrators, so this tells the real app
    /// apart from an impostor squatting on the name.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static bool IsOwnedByAdministrators(NamedPipeClientStream client) =>
        Owner(client) is { } owner && owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid);

    /// <summary>
    /// Whether the connected server end is owned by the current user — the weaker check for an app running without
    /// elevation. Any program running as this user could still have created it, but no other account can: making
    /// an object owned by someone else takes a privilege ordinary users don't have.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static bool IsOwnedByCurrentUser(NamedPipeClientStream client)
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User is { } user && Owner(client) is { } owner && owner == user;
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier? Owner(NamedPipeClientStream client)
    {
        try
        {
            return client.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        }
        catch
        {
            return null;
        }
    }

    public static async Task WriteMessageAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one message (null if the other end closed first).</summary>
    public static async Task<byte[]?> ReadMessageAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        if (!await ReadExactlyOrEofAsync(stream, header, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > maxBytes)
        {
            throw new InvalidDataException($"Message of {length} bytes exceeds the {maxBytes}-byte limit.");
        }

        var payload = new byte[length];
        if (!await ReadExactlyOrEofAsync(stream, payload, cancellationToken).ConfigureAwait(false))
        {
            throw new EndOfStreamException("Truncated message.");
        }

        return payload;
    }

    private static async Task<bool> ReadExactlyOrEofAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return total == 0 && buffer.Length > 0 ? false : throw new EndOfStreamException("Truncated message.");
            }

            total += read;
        }

        return true;
    }

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, AgentJson.Options);

    public static T? Deserialize<T>(byte[] data) => JsonSerializer.Deserialize<T>(data, AgentJson.Options);
}
